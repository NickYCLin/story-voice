using System.Security.Cryptography;
using System.Text;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Worker;

/// <summary>
/// The chapter text and locked-in confirmed plan the Worker needs for one chapter of a
/// multi-character job, in the order the chapters should be narrated.
/// </summary>
public sealed record ChapterPlanSource(
    int ChapterSortOrder,
    ConfirmedSpeechPlanRevision Revision,
    string ChapterTitle,
    string ChapterBody);

/// <summary>
/// Thrown when a locked speech plan no longer matches its own fingerprint, or a segment's
/// recomputed text hash no longer matches the chapter text it should have come from. The Worker
/// treats this as a permanent failure (<c>speech_plan_integrity_mismatch</c>) — it never falls
/// back to "the latest draft" or guesses at recovery.
/// </summary>
public sealed class SpeechPlanIntegrityException(string reasonCode) : InvalidOperationException(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>
/// One confirmed-segment slice folded into a turn: which chapter source text it came from and the
/// exact offsets, plus the segment's story-level identity (kind and speaking character). A merged
/// turn carries one slice per merged segment, in narration order.
/// </summary>
public sealed record NarrationTurnSlice(
    SpeechSegmentSourceKind SourceKind,
    int StartOffset,
    int Length,
    SpeechSegmentTurnKind Kind,
    Guid? CharacterId);

/// <summary>Story-side provenance for one <see cref="NarrationTurn"/>, aligned by index.</summary>
public sealed record NarrationTurnSource(
    Guid ChapterId,
    int ChapterSortOrder,
    bool ChapterStart,
    IReadOnlyList<NarrationTurnSlice> Slices);

/// <summary>
/// The synthesis turns plus, per turn, where its text actually came from. Turns and sources are
/// index-aligned; providers only ever see <see cref="Turns"/>, so cache keys and manifests are
/// unaffected by the provenance data.
/// </summary>
public sealed record MultiCharacterTurnPlan(
    IReadOnlyList<NarrationTurn> Turns,
    IReadOnlyList<NarrationTurnSource> Sources);

/// <summary>
/// Compiles the immutable, job-locked confirmed speech plans for every chapter in a
/// multi-character narration job into an ordered <see cref="NarrationTurn"/> sequence: resolves
/// each segment's full synthesis profile from the job's cast revision, merges adjacent equal-profile turns within a
/// chapter (never across a chapter boundary), and inserts bounded pauses at speaker and chapter
/// changes. Re-verifies plan/segment integrity against the actual chapter text before it will
/// produce any turns at all.
/// </summary>
public static class MultiCharacterTurnBuilder
{
    public const string IntegrityMismatchReasonCode = "speech_plan_integrity_mismatch";
    private const int MaximumMergedTurnLength = 5_000;

    public static IReadOnlyList<NarrationTurn> BuildTurns(
        NarrationCastRevision castRevision,
        IReadOnlyList<ChapterPlanSource> chapterPlans,
        IReadOnlyList<CharacterVoiceProfile>? voiceProfiles = null,
        IReadOnlyDictionary<Guid, Guid>? characterProfileIdsByCharacterId = null) =>
        BuildTurnPlan(castRevision, chapterPlans, voiceProfiles, characterProfileIdsByCharacterId).Turns;

    public static MultiCharacterTurnPlan BuildTurnPlan(
        NarrationCastRevision castRevision,
        IReadOnlyList<ChapterPlanSource> chapterPlans,
        IReadOnlyList<CharacterVoiceProfile>? voiceProfiles = null,
        IReadOnlyDictionary<Guid, Guid>? characterProfileIdsByCharacterId = null)
    {
        ArgumentNullException.ThrowIfNull(castRevision);
        ArgumentNullException.ThrowIfNull(chapterPlans);
        if (chapterPlans.Count == 0)
        {
            throw new SpeechPlanIntegrityException(IntegrityMismatchReasonCode);
        }

        var profilesByCharacter = BuildProfileLookup(
            castRevision,
            voiceProfiles,
            characterProfileIdsByCharacterId);
        var orderedChapters = chapterPlans.OrderBy(plan => plan.ChapterSortOrder).ToArray();
        var turns = new List<NarrationTurn>();
        var sliceLists = new List<List<NarrationTurnSlice>>();
        var sources = new List<NarrationTurnSource>();
        string? previousVoice = null;
        string? previousRate = null;
        string? previousPitch = null;
        string? previousVolume = null;

        foreach (var chapterPlan in orderedChapters)
        {
            if (!chapterPlan.Revision.VerifyFingerprint())
            {
                throw new SpeechPlanIntegrityException(IntegrityMismatchReasonCode);
            }

            var isFirstSegmentOfChapter = true;
            foreach (var segment in chapterPlan.Revision.Segments.OrderBy(candidate => candidate.SortOrder))
            {
                var sourceText = segment.SourceKind == SpeechSegmentSourceKind.ChapterTitle
                    ? chapterPlan.ChapterTitle
                    : chapterPlan.ChapterBody;
                if (segment.StartOffset < 0
                    || segment.Length <= 0
                    || segment.StartOffset > sourceText.Length
                    || segment.Length > sourceText.Length - segment.StartOffset)
                {
                    throw new SpeechPlanIntegrityException(IntegrityMismatchReasonCode);
                }

                var text = sourceText.Substring(segment.StartOffset, segment.Length);
                if (!string.Equals(HashSlice(text), segment.TextHash, StringComparison.Ordinal))
                {
                    throw new SpeechPlanIntegrityException(IntegrityMismatchReasonCode);
                }

                if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetterOrDigit))
                {
                    // Either a paragraph-break artifact from segmentation (e.g. a lone blank
                    // line), or a segment made entirely of punctuation (e.g. a trailing-off
                    // ellipsis quoted as its own dialogue turn, "「......」"). Neither has any
                    // speakable content, and the synthesis provider errors out (or edge-tts
                    // itself returns "no audio was received") when asked to voice it. Skipping
                    // doesn't touch isFirstSegmentOfChapter, so the next real segment still gets
                    // the chapter-boundary pause it would have gotten anyway.
                    continue;
                }

                var contextStart = Math.Max(0, segment.StartOffset - 40);
                var precedingContext = sourceText[contextStart..segment.StartOffset];
                var (voice, rate, pitch, volume) = ResolveVoice(
                    castRevision,
                    segment,
                    text,
                    precedingContext,
                    profilesByCharacter);
                var sameVoiceAsPrevious = voice == previousVoice
                    && rate == previousRate
                    && pitch == previousPitch
                    && volume == previousVolume;
                var canMerge = turns.Count > 0
                    && !isFirstSegmentOfChapter
                    && sameVoiceAsPrevious
                    && turns[^1].Text.Length + text.Length <= MaximumMergedTurnLength;

                var slice = new NarrationTurnSlice(
                    segment.SourceKind,
                    segment.StartOffset,
                    segment.Length,
                    segment.Kind,
                    segment.CharacterId);
                if (canMerge)
                {
                    turns[^1] = turns[^1] with { Text = turns[^1].Text + text };
                    sliceLists[^1].Add(slice);
                }
                else
                {
                    var pauseBeforeMs = turns.Count == 0
                        ? 0
                        : isFirstSegmentOfChapter
                            ? castRevision.ChapterPauseMs
                            : sameVoiceAsPrevious ? 0 : castRevision.DefaultSpeakerPauseMs;
                    turns.Add(new NarrationTurn(text, voice, rate, pitch, volume, pauseBeforeMs));
                    // The source record holds the same list instance, so later merges into this
                    // turn keep its slice list in sync without rebuilding the record.
                    sliceLists.Add([slice]);
                    sources.Add(new NarrationTurnSource(
                        chapterPlan.Revision.ChapterId,
                        chapterPlan.ChapterSortOrder,
                        isFirstSegmentOfChapter,
                        sliceLists[^1]));
                }

                previousVoice = voice;
                previousRate = rate;
                previousPitch = pitch;
                previousVolume = volume;
                isFirstSegmentOfChapter = false;
            }
        }

        return new MultiCharacterTurnPlan(turns, sources);
    }

    private static (string Voice, string Rate, string Pitch, string Volume) ResolveVoice(
        NarrationCastRevision castRevision,
        ConfirmedSpeechSegment segment,
        string segmentText,
        string precedingContext,
        IReadOnlyDictionary<Guid, CharacterVoiceProfileBundle> profilesByCharacter)
    {
        if ((segment.Kind is SpeechSegmentTurnKind.Dialogue or SpeechSegmentTurnKind.InnerMonologue)
            && segment.CharacterId is Guid characterId)
        {
            var assignment = castRevision.Assignments
                .SingleOrDefault(candidate => candidate.CharacterId == characterId);
            if (assignment is not null)
            {
                // Inner/document reading uses the POV character's neutral/base delivery. It is not
                // spoken dialogue, so punctuation or nearby narrative words must not select an
                // angry/happy scene profile or apply dialogue-only synthesis deltas.
                var emotion = segment.Kind == SpeechSegmentTurnKind.InnerMonologue
                    ? DialogueEmotion.Neutral
                    : DialogueEmotionClassifier.Classify(segmentText, precedingContext);
                var isCustomVoiceProvider = string.Equals(
                    assignment.VoiceProvider,
                    CharacterVoiceProviders.ThreeWaVoxCpm2,
                    StringComparison.OrdinalIgnoreCase);
                var isBlueMagpieProvider = string.Equals(
                    assignment.VoiceProvider,
                    CharacterVoiceProviders.BlueMagpie,
                    StringComparison.Ordinal);

                if (isBlueMagpieProvider)
                {
                    // BlueMagpie has no native rate/pitch controls. Its production contract uses
                    // neutral fixed cast parameters, and emotion must never inject an unsupported
                    // delta that the provider would either ignore or interpret inconsistently.
                    return (
                        assignment.Voice,
                        assignment.Rate,
                        assignment.Pitch,
                        assignment.Volume);
                }

                if (isCustomVoiceProvider)
                {
                    if (profilesByCharacter.TryGetValue(characterId, out var bundle))
                    {
                        var sceneCode = DialogueEmotionClassifier.ToSceneCode(emotion);
                        var profile = bundle.Scenes.GetValueOrDefault(sceneCode) ?? bundle.Base;
                        if (profile is not null)
                        {
                            // The emotion is already baked into which cloned/designed voice was
                            // picked, so — unlike the Edge path below — the fixed cast rate/
                            // pitch/volume pass through unchanged rather than getting a delta.
                            return (
                                EncodeVoiceReference(profile),
                                assignment.Rate,
                                assignment.Pitch,
                                assignment.Volume);
                        }
                    }

                    if (segment.Kind == SpeechSegmentTurnKind.InnerMonologue)
                    {
                        throw new SpeechPlanIntegrityException(IntegrityMismatchReasonCode);
                    }

                    // A custom-provider character with no Ready profile yet has no usable Voice
                    // value at all (unlike Edge, assignment.Voice isn't a real voice id for this
                    // provider). Dialogue keeps its narrator fallback below; inner monologue must
                    // fail closed because silently changing the POV voice would corrupt the
                    // confirmed plan's delivery semantics.
                }
                else
                {
                    var (rateDelta, pitchDelta, volumeDelta) = DialogueEmotionClassifier.ToDeltas(emotion);
                    return (
                        assignment.Voice,
                        SynthesisParameterMath.CombinePercent(assignment.Rate, rateDelta),
                        SynthesisParameterMath.CombineHz(assignment.Pitch, pitchDelta),
                        SynthesisParameterMath.CombinePercent(assignment.Volume, volumeDelta));
                }
            }
        }

        if (segment.Kind == SpeechSegmentTurnKind.InnerMonologue)
        {
            throw new SpeechPlanIntegrityException(IntegrityMismatchReasonCode);
        }

        // Narrator segments, and dialogue a human explicitly confirmed as narrator fallback (or
        // whose assigned character was removed from the cast revision, or whose custom-provider
        // profile isn't Ready yet), safely default to the narrator's own fixed voice rather than
        // guessing. Inner monologue is deliberately excluded above: its confirmed POV voice is
        // part of the plan and must never be replaced silently.
        return (
            castRevision.NarratorVoice,
            castRevision.NarratorRate,
            castRevision.NarratorPitch,
            castRevision.NarratorVolume);
    }

    /// <summary>Packs a Ready Clone profile into the opaque reference carried by a narration turn.
    /// Design profiles are rejected while the provider contract cannot distinguish voice prompts.</summary>
    private static string EncodeVoiceReference(CharacterVoiceProfile profile)
    {
        if (profile.Mode != CharacterVoiceProfileMode.Clone)
        {
            throw UnsupportedDesignVoice();
        }

        return $"clone:{profile.VoiceProfileTaskId}";
    }

    /// <summary>
    /// Voice profiles hang off a <c>CharacterProfile</c> library entry, not off any one series'
    /// <c>SeriesCharacter</c> — but <see cref="ConfirmedSpeechSegment.CharacterId"/> (and therefore
    /// <see cref="NarrationCastAssignment.CharacterId"/>) always refers to the series-scoped
    /// character. <paramref name="characterProfileIdsByCharacterId"/> is that one indirection
    /// (SeriesCharacter.Id → CharacterProfile.Id, only present for characters actually linked to a
    /// library entry), letting this stay a single dictionary keyed by series character id, exactly
    /// like <see cref="ResolveVoice"/> already expects.
    /// </summary>
    private static Dictionary<Guid, CharacterVoiceProfileBundle> BuildProfileLookup(
        NarrationCastRevision castRevision,
        IReadOnlyList<CharacterVoiceProfile>? voiceProfiles,
        IReadOnlyDictionary<Guid, Guid>? characterProfileIdsByCharacterId)
    {
        var result = new Dictionary<Guid, CharacterVoiceProfileBundle>();
        var threeWaCharacterIds = castRevision.Assignments
            .Where(assignment => string.Equals(
                assignment.VoiceProvider,
                CharacterVoiceProviders.ThreeWaVoxCpm2,
                StringComparison.OrdinalIgnoreCase))
            .Select(assignment => assignment.CharacterId)
            .ToHashSet();
        if (threeWaCharacterIds.Count == 0)
        {
            return result;
        }

        if (voiceProfiles is null || characterProfileIdsByCharacterId is null)
        {
            throw MissingCloneVoice();
        }

        var threeWaCharacterProfileIds = characterProfileIdsByCharacterId
            .Where(entry => threeWaCharacterIds.Contains(entry.Key))
            .Select(entry => entry.Value)
            .ToHashSet();
        if (threeWaCharacterProfileIds.Count != threeWaCharacterIds.Count)
        {
            throw MissingCloneVoice();
        }

        var bundlesByCharacterProfileId = new Dictionary<Guid, CharacterVoiceProfileBundle>();
        foreach (var profile in voiceProfiles)
        {
            if (!threeWaCharacterProfileIds.Contains(profile.CharacterProfileId))
            {
                continue;
            }

            if (profile.Mode == CharacterVoiceProfileMode.Design)
            {
                throw UnsupportedDesignVoice();
            }

            if (profile.Status != CharacterVoiceProfileStatus.Ready)
            {
                continue;
            }

            if (!bundlesByCharacterProfileId.TryGetValue(profile.CharacterProfileId, out var bundle))
            {
                bundle = new CharacterVoiceProfileBundle();
                bundlesByCharacterProfileId[profile.CharacterProfileId] = bundle;
            }

            if (profile.Kind == CharacterVoiceProfileKind.Base)
            {
                bundle.Base = profile;
            }
            else if (profile.SceneCode is not null)
            {
                bundle.Scenes[profile.SceneCode] = profile;
            }
        }

        foreach (var characterId in threeWaCharacterIds)
        {
            if (!characterProfileIdsByCharacterId.TryGetValue(characterId, out var characterProfileId)
                || !bundlesByCharacterProfileId.TryGetValue(characterProfileId, out var bundle)
                || bundle.Base is null)
            {
                throw MissingCloneVoice();
            }

            result[characterId] = bundle;
        }

        return result;
    }

    private static PermanentNarrationProviderException UnsupportedDesignVoice() =>
        new(
            ThreeWaSynthesisCapabilities.DesignVoiceUnavailableCode,
            ThreeWaSynthesisCapabilities.DesignVoiceUnavailableMessage);

    private static PermanentNarrationProviderException MissingCloneVoice() =>
        new(
            ThreeWaSynthesisCapabilities.CloneVoiceUnavailableCode,
            ThreeWaSynthesisCapabilities.CloneVoiceUnavailableMessage);

    private sealed class CharacterVoiceProfileBundle
    {
        public CharacterVoiceProfile? Base { get; set; }

        public Dictionary<string, CharacterVoiceProfile> Scenes { get; } = new(StringComparer.Ordinal);
    }

    private static string HashSlice(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

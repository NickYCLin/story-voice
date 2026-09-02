using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class MultiCharacterTurnBuilderTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid BookId = Guid.NewGuid();
    private static readonly Guid AliceId = Guid.NewGuid();
    private static readonly Guid AliceCharacterProfileId = Guid.NewGuid();
    private static readonly ChineseSpeechSegmenter Segmenter = new();

    [Fact]
    public void Narrator_and_dialogue_segments_resolve_to_the_narrator_and_assigned_character_voices()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        var chapter = BuildConfirmedChapter(0, "序章", "「你回來了？」艾莉絲說。", AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]);

        Assert.True(turns.Count >= 2);
        Assert.Equal("narrator-voice", turns[0].Voice);
        Assert.Equal("-2Hz", turns[0].Pitch);
        Assert.Equal("-3%", turns[0].Volume);
        var characterTurn = Assert.Single(turns, turn => turn.Voice == "alice-voice");
        Assert.Equal("+4Hz", characterTurn.Pitch);
        Assert.Equal("+2%", characterTurn.Volume);
    }

    [Fact]
    public void Inner_monologue_uses_the_point_of_view_character_voice_without_becoming_dialogue_review()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        var chapter = BuildConfirmedChapter(
            0,
            "序章",
            "最厚的一迭資料叫做『新生入學介紹與如何自保』。我繼續往下看。",
            AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]);

        var innerVoiceTurn = Assert.Single(turns, turn => turn.Voice == "alice-voice");
        Assert.Contains("新生入學介紹與如何自保", innerVoiceTurn.Text);
        Assert.DoesNotContain("最厚的一迭資料叫做", innerVoiceTurn.Text);
        Assert.Equal("+0%", innerVoiceTurn.Rate);
        Assert.Equal("+4Hz", innerVoiceTurn.Pitch);
        Assert.Equal("+2%", innerVoiceTurn.Volume);
    }

    [Fact]
    public void Inner_monologue_ignores_dialogue_emotion_cues_and_keeps_the_base_character_delivery()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        var chapter = BuildConfirmedChapter(
            0,
            "序章",
            "封口上面怒氣沖沖地用紅筆寫了幾個大字。『摔者死！！』多麼簡潔。",
            AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]);

        var innerVoiceTurn = Assert.Single(turns, turn => turn.Voice == "alice-voice");
        Assert.Equal("+0%", innerVoiceTurn.Rate);
        Assert.Equal("+4Hz", innerVoiceTurn.Pitch);
        Assert.Equal("+2%", innerVoiceTurn.Volume);
    }

    [Fact]
    public void BlueMagpie_dialogue_keeps_the_fixed_cast_delivery_without_emotion_deltas()
    {
        var castRevision = BuildCastRevision(
            narratorVoice: "hung_yi_lee",
            aliceVoice: "female_voice",
            aliceProvider: CharacterVoiceProviders.BlueMagpie,
            narratorProvider: CharacterVoiceProviders.BlueMagpie);
        var chapter = BuildConfirmedChapter(0, "序章", "「你們搞什麼鬼！」艾莉絲怒道。", AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]);

        var dialogue = Assert.Single(turns, turn => turn.Voice == "female_voice");
        Assert.Equal("+0%", dialogue.Rate);
        Assert.Equal("+4Hz", dialogue.Pitch);
        Assert.Equal("+2%", dialogue.Volume);
    }

    [Fact]
    public void Adjacent_same_voice_segments_within_a_chapter_merge_into_one_turn()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        // Two consecutive narrator sentences with no dialogue in between should merge.
        var chapter = BuildConfirmedChapter(0, "序章", "風穿過長廊。吹熄了燈。", AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]);

        var narratorTurns = turns.Where(turn => turn.Voice == "narrator-voice").ToArray();
        Assert.Single(narratorTurns);
        Assert.Contains("風穿過長廊", narratorTurns[0].Text);
        Assert.Contains("吹熄了燈", narratorTurns[0].Text);
    }

    [Fact]
    public void Same_voice_segments_never_merge_across_a_chapter_boundary()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        // Chapter 1 ends on a narrator turn ("艾莉絲說。"); chapter 2 opens on a narrator turn too
        // (its title). Both are the same voice and would normally merge — except one is the first
        // segment of a new chapter, which must always force a fresh turn.
        var chapterOne = BuildConfirmedChapter(0, "第一章", "「你好。」艾莉絲說。", AliceId);
        var chapterTwo = BuildConfirmedChapter(1, "第二章", "雨還在下。", AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapterOne, chapterTwo]);

        var narratorTurns = turns.Where(turn => turn.Voice == "narrator-voice").ToArray();
        Assert.Equal(3, narratorTurns.Length); // chapter-1 title, chapter-1 trailing narration, chapter-2 title+body
        var chapterTwoTurn = Assert.Single(turns, turn => turn.Text.Contains("第二章"));
        Assert.DoesNotContain("艾莉絲說", chapterTwoTurn.Text);
        Assert.Equal(castRevision.ChapterPauseMs, chapterTwoTurn.PauseBeforeMs);
    }

    [Fact]
    public void Chapter_boundary_uses_the_chapter_pause_and_speaker_change_uses_the_speaker_pause()
    {
        var castRevision = BuildCastRevision(
            narratorVoice: "narrator-voice",
            aliceVoice: "alice-voice",
            defaultSpeakerPauseMs: 200,
            chapterPauseMs: 900);
        var chapterOne = BuildConfirmedChapter(0, "第一章", "「你回來了？」艾莉絲說。", AliceId);
        var chapterTwo = BuildConfirmedChapter(1, "第二章", "雨還在下。", AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapterOne, chapterTwo]);

        Assert.Equal(0, turns[0].PauseBeforeMs); // very first turn overall: no pause needed
        Assert.Contains(turns, turn => turn.PauseBeforeMs == 200); // narrator -> Alice speaker change
        var secondChapterTitleTurnIndex = turns.ToList().FindIndex(turn => turn.Text.Contains("第二章"));
        Assert.Equal(900, turns[secondChapterTitleTurnIndex].PauseBeforeMs);
    }

    [Fact]
    public void A_dialogue_segment_with_no_matching_cast_assignment_safely_falls_back_to_the_narrator_voice()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        var removedCharacterId = Guid.NewGuid();
        var chapter = BuildConfirmedChapter(0, "序章", "「快走。」他說。", removedCharacterId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]);

        Assert.All(turns, turn => Assert.Equal("narrator-voice", turn.Voice));
    }

    [Fact]
    public void Inner_monologue_with_no_matching_cast_assignment_fails_closed_instead_of_using_the_narrator_voice()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        var removedCharacterId = Guid.NewGuid();
        var chapter = BuildConfirmedChapter(
            0,
            "序章",
            "最厚的一迭資料叫做『新生入學介紹與如何自保』。我繼續往下看。",
            removedCharacterId);

        var exception = Assert.Throws<SpeechPlanIntegrityException>(
            () => MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]));

        Assert.Equal(
            MultiCharacterTurnBuilder.IntegrityMismatchReasonCode,
            exception.ReasonCode);
    }

    [Fact]
    public void Throws_integrity_exception_when_the_chapter_title_changed_after_confirmation()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        var chapter = BuildConfirmedChapter(0, "序章", "「你回來了？」艾莉絲說。", AliceId);
        // Same length as the original title (so offsets still fit) but different content, so the
        // recomputed segment text hash can never match what was hashed at confirmation time.
        var tamperedChapter = chapter with { ChapterTitle = "偽章" };

        var exception = Assert.Throws<SpeechPlanIntegrityException>(
            () => MultiCharacterTurnBuilder.BuildTurns(castRevision, [tamperedChapter]));
        Assert.Equal(MultiCharacterTurnBuilder.IntegrityMismatchReasonCode, exception.ReasonCode);
    }

    [Fact]
    public void Throws_integrity_exception_when_a_segment_offset_no_longer_fits_the_chapter_text()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        var chapter = BuildConfirmedChapter(0, "序章", "「你回來了？」艾莉絲說。", AliceId);
        var truncatedChapter = chapter with { ChapterBody = "短" };

        Assert.Throws<SpeechPlanIntegrityException>(
            () => MultiCharacterTurnBuilder.BuildTurns(castRevision, [truncatedChapter]));
    }

    [Fact]
    public void An_angry_line_gets_a_distinct_rate_and_pitch_from_a_neutral_line_by_the_same_character_and_they_do_not_merge()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        var chapter = BuildConfirmedChapter(
            0,
            "序章",
            "「你回來了？」艾莉絲說。「你給我閉嘴！！」艾莉絲吼道。",
            AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]);

        var aliceTurns = turns.Where(turn => turn.Voice == "alice-voice").ToArray();
        Assert.Equal(2, aliceTurns.Length);
        Assert.Equal("+0%", aliceTurns[0].Rate);
        Assert.Equal("+10%", aliceTurns[1].Rate);
        Assert.Equal("-1Hz", aliceTurns[1].Pitch);
    }

    [Fact]
    public void A_punctuation_only_dialogue_line_never_becomes_its_own_turn()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        // A trailing-off ellipsis quoted as its own dialogue turn has no letters or digits at
        // all — edge-tts returns "no audio was received" for text like this, so it must never
        // reach the synthesis provider as its own turn.
        var chapter = BuildConfirmedChapter(0, "序章", "「你回來了？」艾莉絲說。「......」", AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]);

        Assert.All(turns, turn => Assert.Contains(turn.Text, char.IsLetterOrDigit));
    }

    [Fact]
    public void A_blank_line_paragraph_break_never_becomes_its_own_turn()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        // A blank line between paragraphs makes the segmenter emit a lone "\n" narrator segment —
        // that must never reach the synthesis provider, which rejects blank turn text outright.
        var chapter = BuildConfirmedChapter(0, "序章", "風穿過長廊。\n\n吹熄了燈。", AliceId);

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]);

        Assert.All(turns, turn => Assert.False(string.IsNullOrWhiteSpace(turn.Text)));
    }

    [Fact]
    public void Throws_integrity_exception_for_an_empty_chapter_plan_list()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");

        Assert.Throws<SpeechPlanIntegrityException>(
            () => MultiCharacterTurnBuilder.BuildTurns(castRevision, []));
    }

    [Fact]
    public void An_angry_line_for_a_custom_provider_character_resolves_to_its_angry_scene_profile()
    {
        var castRevision = BuildCastRevision(
            narratorVoice: "narrator-voice",
            aliceVoice: "alice-voice",
            aliceProvider: "3wa-voxcpm2");
        var chapter = BuildConfirmedChapter(0, "序章", "「你給我閉嘴！！」艾莉絲吼道。", AliceId);
        var voiceProfiles = new[]
        {
            BuildReadyProfile(CharacterVoiceProfileKind.Base, sceneCode: null, taskId: "base-task"),
            BuildReadyProfile(CharacterVoiceProfileKind.Scene, CharacterVoiceSceneCodes.Angry, taskId: "angry-task"),
        };
        var characterProfileIdsByCharacterId = new Dictionary<Guid, Guid> { [AliceId] = AliceCharacterProfileId };

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter], voiceProfiles, characterProfileIdsByCharacterId);

        var aliceTurn = Assert.Single(turns, turn => turn.Voice.StartsWith("clone:", StringComparison.Ordinal));
        Assert.Equal("clone:angry-task", aliceTurn.Voice);
        // Emotion is already baked into which cloned voice was picked, so the fixed cast
        // rate/pitch/volume must pass through unchanged rather than getting an emotion delta.
        Assert.Equal("+0%", aliceTurn.Rate);
        Assert.Equal("+4Hz", aliceTurn.Pitch);
    }

    [Fact]
    public void A_neutral_line_for_a_custom_provider_character_without_a_matching_scene_falls_back_to_its_base_profile()
    {
        var castRevision = BuildCastRevision(
            narratorVoice: "narrator-voice",
            aliceVoice: "alice-voice",
            aliceProvider: "3wa-voxcpm2");
        var chapter = BuildConfirmedChapter(0, "序章", "「你回來了？」艾莉絲說。", AliceId);
        var voiceProfiles = new[]
        {
            BuildReadyProfile(CharacterVoiceProfileKind.Base, sceneCode: null, taskId: "base-task"),
        };
        var characterProfileIdsByCharacterId = new Dictionary<Guid, Guid> { [AliceId] = AliceCharacterProfileId };

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter], voiceProfiles, characterProfileIdsByCharacterId);

        var aliceTurn = Assert.Single(turns, turn => turn.Voice.StartsWith("clone:", StringComparison.Ordinal));
        Assert.Equal("clone:base-task", aliceTurn.Voice);
    }

    [Fact]
    public void An_edge_assignment_ignores_a_linked_legacy_design_profile()
    {
        var castRevision = BuildCastRevision(
            narratorVoice: "narrator-voice",
            aliceVoice: "alice-edge-voice",
            aliceProvider: "edge");
        var chapter = BuildConfirmedChapter(0, "序章", "「你回來了？」艾莉絲說。", AliceId);
        var characterProfileIdsByCharacterId = new Dictionary<Guid, Guid> { [AliceId] = AliceCharacterProfileId };

        var turns = MultiCharacterTurnBuilder.BuildTurns(
            castRevision,
            [chapter],
            [BuildDesignedProfile()],
            characterProfileIdsByCharacterId);

        Assert.Contains(turns, turn => turn.Voice == "alice-edge-voice");
    }

    [Fact]
    public void A_three_wa_assignment_with_a_linked_design_profile_fails_closed()
    {
        var castRevision = BuildCastRevision(
            narratorVoice: "narrator-voice",
            aliceVoice: "custom",
            aliceProvider: CharacterVoiceProviders.ThreeWaVoxCpm2);
        var chapter = BuildConfirmedChapter(0, "序章", "「你回來了？」艾莉絲說。", AliceId);
        var characterProfileIdsByCharacterId = new Dictionary<Guid, Guid> { [AliceId] = AliceCharacterProfileId };

        var exception = Assert.Throws<PermanentNarrationProviderException>(
            () => MultiCharacterTurnBuilder.BuildTurns(
                castRevision,
                [chapter],
                [BuildDesignedProfile()],
                characterProfileIdsByCharacterId));

        Assert.Equal(ThreeWaSynthesisCapabilities.DesignVoiceUnavailableCode, exception.ErrorCode);
    }

    [Fact]
    public void Inner_monologue_for_a_custom_provider_uses_base_even_when_text_looks_angry()
    {
        var castRevision = BuildCastRevision(
            narratorVoice: "narrator-voice",
            aliceVoice: "alice-voice",
            aliceProvider: "3wa-voxcpm2");
        var chapter = BuildConfirmedChapter(
            0,
            "序章",
            "封口上面怒氣沖沖地用紅筆寫了幾個大字。『摔者死！！』多麼簡潔。",
            AliceId);
        var voiceProfiles = new[]
        {
            BuildReadyProfile(CharacterVoiceProfileKind.Base, sceneCode: null, taskId: "base-task"),
            BuildReadyProfile(CharacterVoiceProfileKind.Scene, CharacterVoiceSceneCodes.Angry, taskId: "angry-task"),
        };
        var characterProfileIdsByCharacterId = new Dictionary<Guid, Guid> { [AliceId] = AliceCharacterProfileId };

        var turns = MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter], voiceProfiles, characterProfileIdsByCharacterId);

        var aliceTurn = Assert.Single(turns, turn => turn.Voice.StartsWith("clone:", StringComparison.Ordinal));
        Assert.Equal("clone:base-task", aliceTurn.Voice);
    }

    [Fact]
    public void Inner_monologue_for_a_custom_provider_with_no_ready_neutral_or_base_profile_fails_closed()
    {
        var castRevision = BuildCastRevision(
            narratorVoice: "narrator-voice",
            aliceVoice: "alice-voice",
            aliceProvider: "3wa-voxcpm2");
        var chapter = BuildConfirmedChapter(
            0,
            "序章",
            "封口上面怒氣沖沖地用紅筆寫了幾個大字。『摔者死！！』多麼簡潔。",
            AliceId);
        var pendingBaseProfile = BuildPendingProfile(CharacterVoiceProfileKind.Base, sceneCode: null);
        var readyAngryProfile = BuildReadyProfile(
            CharacterVoiceProfileKind.Scene,
            CharacterVoiceSceneCodes.Angry,
            taskId: "angry-task");
        var characterProfileIdsByCharacterId = new Dictionary<Guid, Guid> { [AliceId] = AliceCharacterProfileId };

        var exception = Assert.Throws<PermanentNarrationProviderException>(
            () => MultiCharacterTurnBuilder.BuildTurns(
                castRevision,
                [chapter],
                [pendingBaseProfile, readyAngryProfile],
                characterProfileIdsByCharacterId));

        Assert.Equal(
            ThreeWaSynthesisCapabilities.CloneVoiceUnavailableCode,
            exception.ErrorCode);
    }

    [Fact]
    public void A_custom_provider_character_with_no_ready_profile_fails_closed_instead_of_using_the_narrator()
    {
        var castRevision = BuildCastRevision(
            narratorVoice: "narrator-voice",
            aliceVoice: "alice-voice",
            aliceProvider: "3wa-voxcpm2");
        var chapter = BuildConfirmedChapter(0, "序章", "「你回來了？」艾莉絲說。", AliceId);

        var exception = Assert.Throws<PermanentNarrationProviderException>(
            () => MultiCharacterTurnBuilder.BuildTurns(castRevision, [chapter]));

        Assert.Equal(
            ThreeWaSynthesisCapabilities.CloneVoiceUnavailableCode,
            exception.ErrorCode);
    }

    [Fact]
    public void BuildTurnPlan_sources_align_with_turns_and_carry_chapter_and_slice_provenance()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        var chapterOne = BuildConfirmedChapter(0, "第一章", "「你回來了？」艾莉絲說。", AliceId);
        var chapterTwo = BuildConfirmedChapter(1, "第二章", "雨還在下。", AliceId);

        var plan = MultiCharacterTurnBuilder.BuildTurnPlan(castRevision, [chapterOne, chapterTwo]);

        Assert.Equal(plan.Turns.Count, plan.Sources.Count);
        Assert.True(plan.Sources[0].ChapterStart);
        Assert.Equal(0, plan.Sources[0].ChapterSortOrder);
        Assert.Equal(SpeechSegmentSourceKind.ChapterTitle, plan.Sources[0].Slices[0].SourceKind);

        var dialogueTurnIndex = plan.Turns.ToList().FindIndex(turn => turn.Voice == "alice-voice");
        var dialogueSlice = Assert.Single(plan.Sources[dialogueTurnIndex].Slices);
        Assert.Equal(SpeechSegmentTurnKind.Dialogue, dialogueSlice.Kind);
        Assert.Equal(AliceId, dialogueSlice.CharacterId);

        var chapterTwoTurnIndex = plan.Turns.ToList().FindIndex(turn => turn.Text.Contains("第二章"));
        Assert.True(plan.Sources[chapterTwoTurnIndex].ChapterStart);
        Assert.Equal(1, plan.Sources[chapterTwoTurnIndex].ChapterSortOrder);
        Assert.NotEqual(plan.Sources[0].ChapterId, plan.Sources[chapterTwoTurnIndex].ChapterId);
        // Chapter 2's title and body merge into one narrator turn, so its source must carry both
        // slices — the title slice from the title text and the body slice from the body text.
        Assert.Equal(2, plan.Sources[chapterTwoTurnIndex].Slices.Count);
        Assert.Equal(SpeechSegmentSourceKind.ChapterTitle, plan.Sources[chapterTwoTurnIndex].Slices[0].SourceKind);
        Assert.Equal(SpeechSegmentSourceKind.Body, plan.Sources[chapterTwoTurnIndex].Slices[1].SourceKind);
    }

    [Fact]
    public void BuildTurnPlan_merged_slices_reassemble_the_exact_turn_text_from_the_chapter_sources()
    {
        var castRevision = BuildCastRevision(narratorVoice: "narrator-voice", aliceVoice: "alice-voice");
        const string title = "序章";
        const string body = "風穿過長廊。吹熄了燈。";
        var chapter = BuildConfirmedChapter(0, title, body, AliceId);

        var plan = MultiCharacterTurnBuilder.BuildTurnPlan(castRevision, [chapter]);

        for (var index = 0; index < plan.Turns.Count; index++)
        {
            var reassembled = string.Concat(plan.Sources[index].Slices.Select(slice =>
                slice.SourceKind == SpeechSegmentSourceKind.ChapterTitle
                    ? title.Substring(slice.StartOffset, slice.Length)
                    : body.Substring(slice.StartOffset, slice.Length)));
            Assert.Equal(plan.Turns[index].Text, reassembled);
        }
    }

    private static NarrationCastRevision BuildCastRevision(
        string narratorVoice,
        string aliceVoice,
        int defaultSpeakerPauseMs = 200,
        int chapterPauseMs = 800,
        string aliceProvider = "edge",
        string narratorProvider = "edge")
    {
        var revisionId = Guid.NewGuid();
        var assignment = NarrationCastAssignment.Create(
            Guid.NewGuid(),
            OwnerId,
            SeriesId,
            revisionId,
            AliceId,
            "艾莉絲",
            aliceProvider,
            "1",
            aliceVoice,
            "+0%",
            "+4Hz",
            "+2%");
        return NarrationCastRevision.Create(
            revisionId,
            OwnerId,
            SeriesId,
            revisionNumber: 1,
            narratorProvider: narratorProvider,
            narratorProviderVersion: "1",
            narratorVoice: narratorVoice,
            narratorRate: "-5%",
            narratorPitch: "-2Hz",
            narratorVolume: "-3%",
            defaultSpeakerPauseMs: defaultSpeakerPauseMs,
            chapterPauseMs: chapterPauseMs,
            compositionVersion: "v1",
            ffmpegProfile: "default",
            createdAt: DateTimeOffset.UtcNow,
            assignments: [assignment]);
    }

    private static ChapterPlanSource BuildConfirmedChapter(
        int chapterSortOrder,
        string title,
        string body,
        Guid dialogueCharacterId)
    {
        var chapterId = Guid.NewGuid();
        var plan = Segmenter.Segment(title, body);
        var segments = new List<DraftSegmentInput>
        {
            new(
                0,
                SpeechSegmentSourceKind.ChapterTitle,
                plan.TitleTurn!.StartOffset,
                plan.TitleTurn.Length,
                HashSlice(title[plan.TitleTurn.StartOffset..(plan.TitleTurn.StartOffset + plan.TitleTurn.Length)]),
                SpeechSegmentTurnKind.Narrator,
                null,
                100,
                SpeechSegmentDecisionSource.Rule,
                SpeechSegmentReviewStatus.Confirmed),
        };
        foreach (var bodySegment in plan.BodySegments)
        {
            var sortOrder = segments.Count;
            var text = body.Substring(bodySegment.StartOffset, bodySegment.Length);
            var kind = bodySegment.Kind switch
            {
                SpeechSegmentKind.Dialogue => SpeechSegmentTurnKind.Dialogue,
                SpeechSegmentKind.InnerMonologue => SpeechSegmentTurnKind.InnerMonologue,
                _ => SpeechSegmentTurnKind.Narrator,
            };
            segments.Add(new DraftSegmentInput(
                sortOrder,
                SpeechSegmentSourceKind.Body,
                bodySegment.StartOffset,
                bodySegment.Length,
                HashSlice(text),
                kind,
                kind is SpeechSegmentTurnKind.Dialogue or SpeechSegmentTurnKind.InnerMonologue
                    ? dialogueCharacterId
                    : null,
                100,
                SpeechSegmentDecisionSource.Rule,
                SpeechSegmentReviewStatus.Confirmed));
        }

        var draft = ChapterSpeechPlanDraft.Create(OwnerId, SeriesId, BookId, chapterId, plan.SourceHash, segments);
        var revision = draft.Confirm(1, DateTimeOffset.UtcNow);
        return new ChapterPlanSource(chapterSortOrder, revision, title, body);
    }

    private static CharacterVoiceProfile BuildReadyProfile(CharacterVoiceProfileKind kind, string? sceneCode, string taskId)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = CharacterVoiceProfile.CreateClone(
            Guid.NewGuid(),
            OwnerId,
            AliceCharacterProfileId,
            kind,
            sceneCode,
            CharacterVoiceConsentTypes.SelfRecorded,
            referenceAudioRelativePath: "2026/08/reference.wav",
            referenceAudioSha256: new string('a', 64),
            referenceAudioDurationSeconds: 8.0,
            rightsConfirmedByUserId: OwnerId,
            now);
        profile.AttachDraftTranscript(taskId, "draft transcript", now);
        profile.ConfirmTranscript("confirmed transcript", now);
        return profile;
    }

    private static CharacterVoiceProfile BuildPendingProfile(CharacterVoiceProfileKind kind, string? sceneCode)
    {
        return CharacterVoiceProfile.CreateClone(
            Guid.NewGuid(),
            OwnerId,
            AliceCharacterProfileId,
            kind,
            sceneCode,
            CharacterVoiceConsentTypes.SelfRecorded,
            referenceAudioRelativePath: "2026/08/reference.wav",
            referenceAudioSha256: new string('a', 64),
            referenceAudioDurationSeconds: 8.0,
            rightsConfirmedByUserId: OwnerId,
            DateTimeOffset.UtcNow);
    }

    private static CharacterVoiceProfile BuildDesignedProfile() =>
        CharacterVoiceProfile.CreateDesign(
            Guid.NewGuid(),
            OwnerId,
            AliceCharacterProfileId,
            CharacterVoiceProfileKind.Base,
            null,
            "溫柔、略帶沙啞的台灣華語女聲",
            DateTimeOffset.UtcNow);

    private static string HashSlice(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
}

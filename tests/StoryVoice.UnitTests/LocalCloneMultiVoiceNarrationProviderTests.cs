using StoryVoice.Domain.Narrations;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class LocalCloneMultiVoiceNarrationProviderTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid CharacterProfileId = Guid.NewGuid();

    [Fact]
    public void Narrator_fallback_turns_use_the_clone_that_voices_the_most_text()
    {
        var pov = MultiCharacterTurnBuilder.EncodeLocalCloneReference(Guid.NewGuid());
        var other = MultiCharacterTurnBuilder.EncodeLocalCloneReference(Guid.NewGuid());
        var references = new Dictionary<string, LocalCloneVoiceReference>
        {
            [pov] = new("/refs/pov.wav", "pov transcript"),
            [other] = new("/refs/other.wav", "other transcript"),
        };
        var turns = new[]
        {
            new NarrationTurn("我走進教室，四周一片安靜，只有窗外的風聲。", pov, "+0%", "+0Hz", "+0%", 0),
            new NarrationTurn("「早。」", other, "+0%", "+0Hz", "+0%", 200),
            new NarrationTurn("「誰在那裡？」", "zh-TW-YunJheNeural", "+0%", "+0Hz", "+0%", 200),
        };

        Assert.Equal(pov, LocalCloneMultiVoiceNarrationProvider.ResolveFallbackReferenceKey(turns, references));
    }

    [Fact]
    public void Fallback_is_absent_when_no_turn_carries_a_known_clone_reference()
    {
        var turns = new[] { new NarrationTurn("旁白", "zh-TW-YunJheNeural", "+0%", "+0Hz", "+0%", 0) };

        Assert.Null(LocalCloneMultiVoiceNarrationProvider.ResolveFallbackReferenceKey(
            turns,
            new Dictionary<string, LocalCloneVoiceReference>()));
    }

    [Theory]
    [InlineData("Atlantis AM8：20", false)]
    [InlineData("......", false)]
    [InlineData("2026/08/13", false)]
    [InlineData("「早。」", true)]
    [InlineData("Atlantis 學園 AM8：20", true)]
    [InlineData("ㄅㄆㄇ", true)]
    public void Only_text_with_cjk_characters_is_sent_to_the_clone_engine(string text, bool speakable)
    {
        Assert.Equal(speakable, LocalCloneMultiVoiceNarrationProvider.HasSpeakableText(text));
    }

    [Fact]
    public void Sentence_fallback_splits_on_terminal_punctuation()
    {
        var pieces = LocalCloneMultiVoiceNarrationProvider.SplitSentences("你好。Atlantis AM8：20！再見？尾巴");

        Assert.Equal(["你好。", "Atlantis AM8：20！", "再見？", "尾巴"], pieces);
    }

    [Fact]
    public void Long_turns_split_on_sentence_boundaries_within_the_chunk_limit()
    {
        var sentence = "這是一個句子。";
        var text = string.Concat(Enumerable.Repeat(sentence, 40));

        var chunks = LocalCloneMultiVoiceNarrationProvider.SplitText(text, 60);

        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 60));
        Assert.All(chunks, chunk => Assert.EndsWith("。", chunk));
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void Worker_maps_ready_clone_profiles_to_their_stored_recording_only_for_the_local_clone_provider()
    {
        var now = DateTimeOffset.UtcNow;
        var ready = CharacterVoiceProfile.CreateClone(
            Guid.NewGuid(),
            OwnerId,
            CharacterProfileId,
            CharacterVoiceProfileKind.Base,
            sceneCode: null,
            CharacterVoiceConsentTypes.SelfRecorded,
            referenceAudioRelativePath: "2026/09/reference.wav",
            referenceAudioSha256: new string('a', 64),
            referenceAudioDurationSeconds: 12.0,
            rightsConfirmedByUserId: OwnerId,
            now);
        ready.AttachDraftTranscript("task", "草稿", now);
        ready.ConfirmTranscript("確認後的逐字稿。", now);
        var pending = CharacterVoiceProfile.CreateClone(
            Guid.NewGuid(),
            OwnerId,
            CharacterProfileId,
            CharacterVoiceProfileKind.Scene,
            CharacterVoiceSceneCodes.Angry,
            CharacterVoiceConsentTypes.SelfRecorded,
            referenceAudioRelativePath: "2026/09/angry.wav",
            referenceAudioSha256: new string('b', 64),
            referenceAudioDurationSeconds: 12.0,
            rightsConfirmedByUserId: OwnerId,
            now);
        var localCloneRevision = BuildCastRevision(CharacterVoiceProviders.LocalClone);
        var root = Path.Combine(Path.GetTempPath(), "storyvoice-voices");

        var references = StoryPipelineWorker.BuildLocalCloneReferences(localCloneRevision, [ready, pending], root);

        Assert.NotNull(references);
        var entry = Assert.Single(references);
        Assert.Equal(MultiCharacterTurnBuilder.EncodeLocalCloneReference(ready.Id), entry.Key);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "2026/09/reference.wav")), entry.Value.ReferenceAudioPath);
        Assert.Equal("確認後的逐字稿。", entry.Value.ReferenceTranscript);

        Assert.Null(StoryPipelineWorker.BuildLocalCloneReferences(
            BuildCastRevision(CharacterVoiceProviders.ThreeWaVoxCpm2),
            [ready],
            root));
    }

    private static NarrationCastRevision BuildCastRevision(string narratorProvider)
    {
        var revisionId = Guid.NewGuid();
        var assignment = NarrationCastAssignment.Create(
            Guid.NewGuid(),
            OwnerId,
            SeriesId,
            revisionId,
            Guid.NewGuid(),
            "艾莉絲",
            narratorProvider,
            "1",
            "zh-TW-YunJheNeural",
            "+0%",
            "+0Hz",
            "+0%");
        return NarrationCastRevision.Create(
            revisionId,
            OwnerId,
            SeriesId,
            revisionNumber: 1,
            narratorProvider: narratorProvider,
            narratorProviderVersion: "1",
            narratorVoice: "zh-TW-YunJheNeural",
            narratorRate: "+0%",
            narratorPitch: "+0Hz",
            narratorVolume: "+0%",
            defaultSpeakerPauseMs: 200,
            chapterPauseMs: 700,
            compositionVersion: "v1",
            ffmpegProfile: "default",
            createdAt: DateTimeOffset.UtcNow,
            assignments: [assignment]);
    }
}

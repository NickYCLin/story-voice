using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Narrations;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class NarrationTimelineTests
{
    private static readonly Guid ChapterOneId = Guid.NewGuid();
    private static readonly Guid ChapterTwoId = Guid.NewGuid();
    private static readonly Guid AliceId = Guid.NewGuid();

    private static IReadOnlyList<NarrationTurnSource> BuildSources() =>
    [
        new NarrationTurnSource(
            ChapterOneId,
            0,
            ChapterStart: true,
            [new NarrationTurnSlice(SpeechSegmentSourceKind.ChapterTitle, 0, 2, SpeechSegmentTurnKind.Narrator, null)]),
        new NarrationTurnSource(
            ChapterOneId,
            0,
            ChapterStart: false,
            [new NarrationTurnSlice(SpeechSegmentSourceKind.Body, 1, 6, SpeechSegmentTurnKind.Dialogue, AliceId)]),
        new NarrationTurnSource(
            ChapterTwoId,
            1,
            ChapterStart: true,
            [
                new NarrationTurnSlice(SpeechSegmentSourceKind.ChapterTitle, 0, 3, SpeechSegmentTurnKind.Narrator, null),
                new NarrationTurnSlice(SpeechSegmentSourceKind.Body, 0, 5, SpeechSegmentTurnKind.Narrator, null),
            ]),
    ];

    [Fact]
    public void Composer_joins_sources_and_timings_into_a_round_trippable_document()
    {
        var timings = new[]
        {
            new NarrationTurnTiming(0, 0, 1_200),
            new NarrationTurnTiming(1, 1_400, 2_600),
            new NarrationTurnTiming(2, 4_900, 3_100),
        };

        var document = NarrationTimelineComposer.Compose(BuildSources(), timings);

        Assert.NotNull(document);
        Assert.Equal(NarrationTimelineDocument.CurrentSchemaVersion, document!.SchemaVersion);
        Assert.Equal(3, document.Turns.Count);
        Assert.True(document.Turns[0].ChapterStart);
        Assert.False(document.Turns[1].ChapterStart);
        Assert.Equal(1_400, document.Turns[1].StartMs);
        Assert.Equal(AliceId, document.Turns[1].Slices[0].CharacterId);
        Assert.Equal(NarrationTimelineTurnKinds.Dialogue, document.Turns[1].Slices[0].Kind);
        Assert.Equal(2, document.Turns[2].Slices.Count);
        Assert.Equal(NarrationTimelineSourceKinds.ChapterTitle, document.Turns[2].Slices[0].SourceKind);

        var roundTripped = NarrationTimelineDocument.FromJson(document.ToJson());
        Assert.NotNull(roundTripped);
        Assert.Equal(document.ToJson(), roundTripped!.ToJson());
    }

    [Fact]
    public void Composer_returns_null_when_the_provider_reported_no_timings()
    {
        Assert.Null(NarrationTimelineComposer.Compose(BuildSources(), null));
    }

    [Fact]
    public void Composer_returns_null_when_timing_and_source_counts_disagree()
    {
        var timings = new[] { new NarrationTurnTiming(0, 0, 1_000) };

        Assert.Null(NarrationTimelineComposer.Compose(BuildSources(), timings));
    }

    [Fact]
    public void Document_rejects_json_with_an_unknown_schema_version()
    {
        Assert.Null(NarrationTimelineDocument.FromJson(
            """{"schemaVersion":"storyvoice:narration-timeline:v999","turns":[]}"""));
    }

    [Fact]
    public void Document_rejects_malformed_json_and_negative_offsets()
    {
        Assert.Null(NarrationTimelineDocument.FromJson("not json"));
        Assert.Null(NarrationTimelineDocument.FromJson(
            $$"""
            {"schemaVersion":"{{NarrationTimelineDocument.CurrentSchemaVersion}}","turns":[
              {"startMs":-1,"durationMs":10,"chapterId":"{{ChapterOneId}}","chapterSortOrder":0,"chapterStart":true,
               "slices":[{"sourceKind":"body","startOffset":0,"length":5,"kind":"narrator","characterId":null}]}]}
            """));
    }

    [Fact]
    public void Edge_provider_parses_a_well_formed_timeline_from_provider_output()
    {
        const string output = """
            {"schemaVersion": "storyvoice:multi-voice-timeline:v1", "turns": [
              {"index": 0, "startMs": 0, "durationMs": 1200},
              {"index": 1, "startMs": 1400, "durationMs": 2600}]}
            """;

        var timings = EdgeTtsMultiVoiceNarrationProvider.TryParseTurnTimings(output, expectedTurnCount: 2);

        Assert.NotNull(timings);
        Assert.Equal(2, timings!.Count);
        Assert.Equal(new NarrationTurnTiming(1, 1_400, 2_600), timings[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"schemaVersion":"storyvoice:multi-voice-timeline:v1","turns":[]}""")]
    [InlineData("""{"schemaVersion":"other","turns":[{"index":0,"startMs":0,"durationMs":1}]}""")]
    [InlineData("""{"schemaVersion":"storyvoice:multi-voice-timeline:v1","turns":[{"index":1,"startMs":0,"durationMs":1}]}""")]
    public void Edge_provider_rejects_missing_or_inconsistent_timelines(string? output)
    {
        Assert.Null(EdgeTtsMultiVoiceNarrationProvider.TryParseTurnTimings(output, expectedTurnCount: 1));
    }

    [Fact]
    public void Edge_provider_rejects_overlapping_turn_timings()
    {
        const string output = """
            {"schemaVersion": "storyvoice:multi-voice-timeline:v1", "turns": [
              {"index": 0, "startMs": 0, "durationMs": 2000},
              {"index": 1, "startMs": 1500, "durationMs": 1000}]}
            """;

        Assert.Null(EdgeTtsMultiVoiceNarrationProvider.TryParseTurnTimings(output, expectedTurnCount: 2));
    }

    [Fact]
    public void Timeline_entity_requires_identities_and_content()
    {
        Assert.Throws<ArgumentException>(() => NarrationTimeline.Create(Guid.Empty, Guid.NewGuid(), "{}"));
        Assert.Throws<ArgumentException>(() => NarrationTimeline.Create(Guid.NewGuid(), Guid.Empty, "{}"));
        Assert.Throws<ArgumentException>(() => NarrationTimeline.Create(Guid.NewGuid(), Guid.NewGuid(), " "));

        var ownerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var timeline = NarrationTimeline.Create(ownerId, jobId, """{"a":1}""");
        Assert.Equal(ownerId, timeline.OwnerId);
        Assert.Equal(jobId, timeline.NarrationJobId);
    }
}

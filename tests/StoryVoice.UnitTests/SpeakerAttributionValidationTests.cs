using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.UnitTests;

public sealed class SpeakerAttributionValidationTests
{
    private static readonly Guid First = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly SpeakerAttributionRequest Request = new(
        [new(First, "小雨", []), new(Second, "小晴", [])],
        [new(0, SpeechSegmentKind.Dialogue, "「你好。」"), new(1, SpeechSegmentKind.Dialogue, "「早安。」")]);

    [Fact]
    public async Task Null_collection_becomes_review_results_for_every_dialogue()
    {
        var result = await new LocalSpeakerAttributionProvider(new Stub(null))
            .AttributeAsync(Request, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.All(result, AssertUnknown);
    }

    [Fact]
    public async Task Duplicate_and_null_rows_do_not_discard_other_valid_segments()
    {
        var good = Result(1, First);
        var result = await new LocalSpeakerAttributionProvider(new Stub([Result(0, First), null!, Result(0, Second), good]))
            .AttributeAsync(Request, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        AssertUnknown(result.Single(item => item.SegmentIndex == 0));
        Assert.Equal(good, result.Single(item => item.SegmentIndex == 1));
        Assert.Equal(2, result.ToDictionary(item => item.SegmentIndex).Count);
    }

    [Theory]
    [InlineData(-1, 0, 1, true)]
    [InlineData(101, 0, 1, true)]
    [InlineData(90, 99, 1, true)]
    [InlineData(90, 0, 99, true)]
    [InlineData(90, 0, 1, false)]
    [InlineData(90, 1, 1, false)]
    [InlineData(0, 2, 1, true)]
    public async Task Invalid_scores_enums_and_identity_outcome_pairs_are_not_confirmed(
        int confidence, int outcome, int source, bool hasCharacter)
    {
        var malformed = new SpeakerAttributionResult(0, hasCharacter ? First : null,
            (SpeakerAttributionOutcome)outcome, confidence, (SpeakerAttributionDecisionSource)source, "synthetic");
        var result = await new LocalSpeakerAttributionProvider(new Stub([malformed, Result(1, Second)]))
            .AttributeAsync(Request, TestContext.Current.CancellationToken);
        AssertUnknown(result.Single(item => item.SegmentIndex == 0));
        Assert.Equal(Second, result.Single(item => item.SegmentIndex == 1).CharacterId);
    }

    [Fact]
    public async Task Hybrid_does_not_choose_the_highest_score_from_conflicting_model_rows()
    {
        var rules = new Stub([Result(0, First) with { Source = SpeakerAttributionDecisionSource.Rule },
            new(1, null, SpeakerAttributionOutcome.Unknown, 0, SpeakerAttributionDecisionSource.Rule, "unknown")]);
        var model = new Stub([Result(1, First) with { Confidence = 99 }, Result(1, Second)]);
        var result = await new HybridSpeakerAttributionProvider(rules, model)
            .AttributeAsync(Request, TestContext.Current.CancellationToken);
        Assert.Equal(First, result.Single(item => item.SegmentIndex == 0).CharacterId);
        AssertUnknown(result.Single(item => item.SegmentIndex == 1));
    }

    [Fact]
    public async Task Hybrid_keeps_rule_results_when_the_model_returns_null()
    {
        SpeakerAttributionResult[] ruleResults = [Result(0, First) with { Source = SpeakerAttributionDecisionSource.Rule },
            new(1, Second, SpeakerAttributionOutcome.Suggested, 55, SpeakerAttributionDecisionSource.Rule, "context")];
        var result = await new HybridSpeakerAttributionProvider(new Stub(ruleResults), new Stub(null))
            .AttributeAsync(Request, TestContext.Current.CancellationToken);
        Assert.Equal(ruleResults, result);
    }

    [Fact]
    public async Task A_model_cannot_label_its_answer_as_a_rule_result()
    {
        SpeakerAttributionResult[] rules = [new(0, null, SpeakerAttributionOutcome.Unknown, 0, SpeakerAttributionDecisionSource.Rule, "unknown")];
        var model = new Stub([Result(0, First) with { Source = SpeakerAttributionDecisionSource.Rule }]);
        var result = await new HybridSpeakerAttributionProvider(new Stub(rules), model)
            .AttributeAsync(Request, TestContext.Current.CancellationToken);
        Assert.Equal(rules, result);
    }

    [Fact]
    public async Task Cancellation_is_honored_even_when_the_inner_provider_returns_normally()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new LocalSpeakerAttributionProvider(new Stub([Result(0, First)], () => cancellation.Cancel()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.AttributeAsync(Request, cancellation.Token));
    }

    private static SpeakerAttributionResult Result(int index, Guid character) =>
        new(index, character, SpeakerAttributionOutcome.Confirmed, 90, SpeakerAttributionDecisionSource.LocalModel, "synthetic");

    private static void AssertUnknown(SpeakerAttributionResult result)
    {
        Assert.Null(result.CharacterId);
        Assert.Equal(0, result.Confidence);
        Assert.Equal(SpeakerAttributionOutcome.Unknown, result.Outcome);
    }

    private sealed class Stub(IReadOnlyList<SpeakerAttributionResult>? result, Action? onCall = null) : ISpeakerAttributionProvider
    {
        public Task<IReadOnlyList<SpeakerAttributionResult>> AttributeAsync(SpeakerAttributionRequest request, CancellationToken cancellationToken)
        {
            onCall?.Invoke();
            return Task.FromResult(result!);
        }
    }
}

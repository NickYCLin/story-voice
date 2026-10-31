using StoryVoice.Application.Narrations.SpeechPlanning;

namespace StoryVoice.Infrastructure.Narrations;

/// <summary>
/// Keeps deterministic high-confidence rules authoritative and asks the local model only to
/// improve unresolved or suggested dialogue turns. A model failure never discards valid rule
/// results; those turns simply remain in the manual-review queue.
/// </summary>
public sealed class HybridSpeakerAttributionProvider(
    ISpeakerAttributionProvider ruleProvider,
    ISpeakerAttributionProvider localModelProvider) : ISpeakerAttributionProvider
{
    public async Task<IReadOnlyList<SpeakerAttributionResult>> AttributeAsync(
        SpeakerAttributionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ruleResults = await ruleProvider.AttributeAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (ruleResults.All(result => result.Outcome == SpeakerAttributionOutcome.Confirmed))
        {
            return ruleResults;
        }

        IReadOnlyList<SpeakerAttributionResult> modelResults;
        try
        {
            modelResults = await localModelProvider.AttributeAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            modelResults = LocalSpeakerAttributionProvider.ValidateResults(
                request, modelResults, SpeakerAttributionDecisionSource.LocalModel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ruleResults;
        }

        var modelByIndex = modelResults.ToDictionary(result => result.SegmentIndex);
        return ruleResults.Select(ruleResult =>
        {
            if (ruleResult.Outcome == SpeakerAttributionOutcome.Confirmed
                || !modelByIndex.TryGetValue(ruleResult.SegmentIndex, out var modelResult))
            {
                return ruleResult;
            }

            if (modelResult.Outcome == SpeakerAttributionOutcome.Confirmed
                || modelResult.CharacterId is not null && modelResult.Confidence > ruleResult.Confidence)
            {
                return modelResult;
            }

            return ruleResult;
        }).ToArray();
    }
}

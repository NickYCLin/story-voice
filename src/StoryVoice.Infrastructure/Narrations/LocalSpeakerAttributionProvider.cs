using StoryVoice.Application.Narrations.SpeechPlanning;

namespace StoryVoice.Infrastructure.Narrations;

/// <summary>
/// Validation around an inner <see cref="ISpeakerAttributionProvider"/> (rules or a local
/// model). Enforces the constraints from the
/// multi-character plan: input is limited to the current series cast, output can only reference
/// a known character ID or resolve to Unknown, and any timeout, exception, malformed result, or
/// out-of-scope character ID from the inner provider is treated as untrusted and safely
/// downgraded to a review-worthy Unknown result rather than propagated or guessed.
/// </summary>
public sealed class LocalSpeakerAttributionProvider(
    ISpeakerAttributionProvider innerProvider,
    TimeSpan? timeout = null) : ISpeakerAttributionProvider
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<SpeakerAttributionResult>> AttributeAsync(
        SpeakerAttributionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var dialogueSegmentIndexes = request.Segments
            .Where(segment => segment.Kind == SpeechSegmentKind.Dialogue)
            .Select(segment => segment.Index)
            .ToHashSet();

        IReadOnlyList<SpeakerAttributionResult>? rawResults;
        try
        {
            using var timeoutSource = new CancellationTokenSource(_timeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            rawResults = await innerProvider.AttributeAsync(request, linkedSource.Token)
                .ConfigureAwait(false);
            linkedSource.Token.ThrowIfCancellationRequested();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation propagates; an inner timeout, crash, or malformed-output
            // exception does not — it degrades to "everything needs review" instead.
            return FallbackToReview(dialogueSegmentIndexes, "attribution_provider_failed");
        }

        return ValidateResults(request, rawResults);
    }

    internal static IReadOnlyList<SpeakerAttributionResult> ValidateResults(
        SpeakerAttributionRequest request,
        IReadOnlyList<SpeakerAttributionResult>? rawResults,
        SpeakerAttributionDecisionSource? expectedSource = null)
    {
        var knownCharacterIds = request.KnownCharacters.Select(character => character.CharacterId).ToHashSet();
        var dialogueSegmentIds = request.Segments.Where(segment => segment.Kind == SpeechSegmentKind.Dialogue)
            .Select(segment => segment.Index).ToHashSet();
        if (rawResults is null)
            return FallbackToReview(dialogueSegmentIds, "attribution_provider_malformed_result");

        var byIndex = new Dictionary<int, SpeakerAttributionResult>();
        var duplicates = new HashSet<int>();
        foreach (var result in rawResults)
        {
            if (result is null || !dialogueSegmentIds.Contains(result.SegmentIndex)) continue;
            if (!byIndex.TryAdd(result.SegmentIndex, result)) duplicates.Add(result.SegmentIndex);
        }

        var validated = new List<SpeakerAttributionResult>(dialogueSegmentIds.Count);
        foreach (var index in dialogueSegmentIds.Order())
        {
            if (duplicates.Contains(index))
            {
                validated.Add(Review(index, "attribution_provider_duplicate_result"));
                continue;
            }
            if (!byIndex.TryGetValue(index, out var result))
            {
                validated.Add(Review(index, "attribution_provider_missing_result"));
                continue;
            }
            if (result.Confidence is < 0 or > 100 || !Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.Source)
                || expectedSource is not null && result.Source != expectedSource
                || (result.Outcome == SpeakerAttributionOutcome.Unknown
                    ? result.CharacterId is not null || result.Confidence != 0
                    : result.CharacterId is null))
            {
                validated.Add(Review(index, "attribution_provider_invalid_result"));
                continue;
            }
            if (result.CharacterId is Guid characterId && !knownCharacterIds.Contains(characterId))
            {
                validated.Add(Review(index, "unknown_character_id_rejected"));
                continue;
            }
            validated.Add(result);
        }
        return validated;
    }

    private static IReadOnlyList<SpeakerAttributionResult> FallbackToReview(
        IReadOnlySet<int> dialogueSegmentIndexes,
        string reasonCode) =>
        dialogueSegmentIndexes.Order()
            .Select(index => Review(index, reasonCode))
            .ToArray();

    private static SpeakerAttributionResult Review(int index, string reasonCode) =>
        new(index, null, SpeakerAttributionOutcome.Unknown, 0, SpeakerAttributionDecisionSource.LocalModel, reasonCode);
}

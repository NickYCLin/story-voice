namespace StoryVoice.Application.Narrations.SpeechPlanning;

public sealed record SpeechPlanSegmentResponse(
    Guid Id,
    int SortOrder,
    string SourceKind,
    string Kind,
    int StartOffset,
    int Length,
    Guid? CharacterId,
    string? CharacterName,
    int Confidence,
    string DecisionSource,
    string ReviewStatus);

public sealed record ChapterSpeechPlanDraftResponse(
    Guid Id,
    Guid SeriesId,
    Guid BookId,
    Guid ChapterId,
    int PlanVersion,
    string Status,
    Guid? ConfirmedRevisionId,
    IReadOnlyList<SpeechPlanSegmentResponse> Segments,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ConfirmedSpeechPlanRevisionResponse(
    Guid Id,
    Guid SeriesId,
    Guid BookId,
    Guid ChapterId,
    int RevisionNumber,
    string Fingerprint,
    int SegmentCount,
    DateTimeOffset CreatedAt);

public sealed record ConfirmSpeechSegmentRequest(Guid? CharacterId);

/// <summary>
/// Bulk-accepts unreviewed dialogue suggestions that already point at a character. A null
/// <see cref="CharacterId"/> accepts every such suggestion in the draft; a value limits the
/// batch to suggestions for that one character.
/// </summary>
public sealed record ConfirmSuggestedSpeechSegmentsRequest(Guid? CharacterId);

/// <summary>
/// User override of a body segment's turn kind and speaker. <see cref="Kind"/> must be one of
/// <c>Narrator</c>, <c>Dialogue</c> or <c>InnerMonologue</c>.
/// </summary>
public sealed record ReassignSpeechSegmentRequest(string Kind, Guid? CharacterId);

public sealed record SpeechSegmentPreviewAudio(byte[] Content, string ContentType);

public interface ISpeechSegmentPreviewService
{
    /// <summary>
    /// Synthesizes one draft segment's text with the voice it would actually use in the staged
    /// rebuild (the assigned character's voice, or the series narrator). Only locally hosted
    /// providers are supported; cloud providers synthesize exclusively inside batch worker jobs.
    /// </summary>
    Task<SpeechSegmentPreviewAudio?> PreviewSegmentAsync(
        Guid seriesId,
        Guid draftId,
        Guid segmentId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The segment's effective voice belongs to a provider the API cannot synthesize with directly
/// (for example Edge or VoAI, which only run inside batch worker jobs).
/// </summary>
public sealed class SpeechSegmentPreviewUnsupportedProviderException(string provider)
    : Exception($"「{provider}」聲線的單句試聽尚未支援；此供應商只在整批合成工作中執行。")
{
    public const string StableCode = "speech_segment_preview_unsupported_provider";

    public string Provider { get; } = provider;
}

namespace StoryVoice.Application.Narrations.SpeechPlanning;

public interface ISpeechPlanService
{
    /// <summary>
    /// Builds a chapter's first draft, or explicitly regenerates it as a new <c>PlanVersion</c>.
    /// Regeneration always refreshes speaker decisions because they also depend on the current
    /// cast, aliases and point-of-view character even when the chapter text is unchanged.
    /// </summary>
    Task<ChapterSpeechPlanDraftResponse?> BuildOrRefreshDraftAsync(
        Guid seriesId,
        Guid bookId,
        Guid chapterId,
        CancellationToken cancellationToken);

    Task<ChapterSpeechPlanDraftResponse?> GetDraftAsync(
        Guid seriesId,
        Guid bookId,
        Guid chapterId,
        CancellationToken cancellationToken);

    Task<ChapterSpeechPlanDraftResponse?> ConfirmSegmentAsync(
        Guid seriesId,
        Guid draftId,
        Guid segmentId,
        ConfirmSpeechSegmentRequest request,
        CancellationToken cancellationToken);

    Task<ChapterSpeechPlanDraftResponse?> RejectSegmentAsync(
        Guid seriesId,
        Guid draftId,
        Guid segmentId,
        CancellationToken cancellationToken);

    Task<ChapterSpeechPlanDraftResponse?> ConfirmSuggestedSegmentsAsync(
        Guid seriesId,
        Guid draftId,
        ConfirmSuggestedSpeechSegmentsRequest request,
        CancellationToken cancellationToken);

    Task<ChapterSpeechPlanDraftResponse?> ReassignSegmentAsync(
        Guid seriesId,
        Guid draftId,
        Guid segmentId,
        ReassignSpeechSegmentRequest request,
        CancellationToken cancellationToken);

    Task<ConfirmedSpeechPlanRevisionResponse?> ConfirmPlanAsync(
        Guid seriesId,
        Guid draftId,
        CancellationToken cancellationToken);
}

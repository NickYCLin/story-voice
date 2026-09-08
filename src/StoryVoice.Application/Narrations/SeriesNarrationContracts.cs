namespace StoryVoice.Application.Narrations;

public sealed record CreateSeriesNarrationRebuildRequest(bool RightsAttested);

public sealed record SeriesNarrationRebuildMemberResponse(
    Guid Id,
    Guid SeriesBookId,
    Guid BookId,
    int MembershipRevision,
    Guid? PreviousActiveNarrationJobId,
    Guid? StagedNarrationJobId,
    string Status);

public sealed record SeriesNarrationRebuildResponse(
    Guid Id,
    Guid SeriesId,
    Guid? BaseActiveCastRevisionId,
    Guid DraftCastRevisionId,
    int CohortMembershipRevision,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SeriesNarrationRebuildMemberResponse> Members);

public interface IStagedNarrationBatchProgressService
{
    Task SynchronizeAsync(Guid narrationJobId, CancellationToken cancellationToken);
}

public interface ISeriesNarrationService
{
    Task<IReadOnlyList<SeriesNarrationRebuildResponse>?> ListRebuildsAsync(
        Guid seriesId, CancellationToken cancellationToken);

    Task<SeriesNarrationRebuildResponse?> RetryRebuildAsync(
        Guid seriesId, Guid batchId, CreateSeriesNarrationRebuildRequest request, CancellationToken cancellationToken);

    Task<SeriesNarrationRebuildResponse?> CreateRebuildAsync(
        Guid seriesId,
        CreateSeriesNarrationRebuildRequest request,
        CancellationToken cancellationToken);

    Task<SeriesNarrationRebuildResponse?> GetRebuildAsync(
        Guid seriesId,
        Guid batchId,
        CancellationToken cancellationToken);

    Task<SeriesNarrationRebuildResponse?> DiscardRebuildAsync(
        Guid seriesId,
        Guid batchId,
        CancellationToken cancellationToken);

    Task<SeriesNarrationRebuildResponse?> ActivateRebuildAsync(
        Guid seriesId,
        Guid batchId,
        CancellationToken cancellationToken);
}

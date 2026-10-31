using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StoryVoice.Domain.Narrations;
using StoryVoice.Domain.Series;

namespace StoryVoice.Infrastructure.Persistence;

internal readonly record struct CastEpochActivationCommand(
    Guid OwnerId,
    Guid SeriesId,
    Guid BatchId,
    DateTimeOffset ActivatedAt);

internal sealed record CastEpochActivationResult(
    Guid SeriesId,
    Guid BatchId,
    Guid ActiveCastRevisionId,
    int EpochNumber,
    DateTimeOffset ActivatedAt);

internal enum CastEpochActivationFailure
{
    InvalidCommand,
    ScopeNotFound,
    BatchNotReady,
    StaleBaseRevision,
    DraftRevisionInvalid,
    BaseRevisionInvalid,
    CohortChanged,
    MemberInvalid,
    StagedArtifactInvalid,
    PreviousPointerChanged,
    PreviousArtifactInvalid,
    EpochStateInvalid
}

internal sealed class CastEpochActivationRejectedException(CastEpochActivationFailure failure)
    : InvalidOperationException("Cast epoch activation was rejected.")
{
    public CastEpochActivationFailure Failure { get; } = failure;
}

internal sealed class PostgreSqlCastEpochActivationPublisher(StoryVoiceDbContext dbContext)
{
    private const int MaximumAttempts = 3;

    public async Task<CastEpochActivationResult> ActivateAsync(
        CastEpochActivationCommand command,
        CancellationToken cancellationToken)
    {
        ValidateAdmission(command);

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                return await ActivateOnceAsync(command, cancellationToken);
            }
            catch (Exception exception) when (
                attempt < MaximumAttempts && IsRetryablePostgreSqlFailure(exception))
            {
                dbContext.ChangeTracker.Clear();
            }
            catch
            {
                dbContext.ChangeTracker.Clear();
                throw;
            }
        }

        throw new InvalidOperationException("The cast epoch activation retry loop terminated unexpectedly.");
    }

    private void ValidateAdmission(CastEpochActivationCommand command)
    {
        if (command.OwnerId == Guid.Empty
            || command.SeriesId == Guid.Empty
            || command.BatchId == Guid.Empty)
        {
            dbContext.ChangeTracker.Clear();
            Reject(CastEpochActivationFailure.InvalidCommand);
        }

        if (dbContext.ChangeTracker.Entries().Any(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            Reject(CastEpochActivationFailure.InvalidCommand);
        }

        dbContext.ChangeTracker.Clear();
    }

    private async Task<CastEpochActivationResult> ActivateOnceAsync(
        CastEpochActivationCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var series = await dbContext.StorySeries
            .FromSqlInterpolated($"""
                SELECT *
                FROM story_series
                WHERE "OwnerId" = {command.OwnerId}
                    AND "Id" = {command.SeriesId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (series is null)
        {
            Reject(CastEpochActivationFailure.ScopeNotFound);
        }

        var batch = await dbContext.SeriesCastRebuildBatches
            .FromSqlInterpolated($"""
                SELECT *
                FROM series_cast_rebuild_batches
                WHERE "OwnerId" = {command.OwnerId}
                    AND "SeriesId" = {command.SeriesId}
                    AND "Id" = {command.BatchId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            Reject(CastEpochActivationFailure.ScopeNotFound);
        }

        var seriesBooks = await dbContext.SeriesBooks
            .FromSqlInterpolated($"""
                SELECT *
                FROM series_books
                WHERE "OwnerId" = {command.OwnerId}
                    AND "SeriesId" = {command.SeriesId}
                ORDER BY "Id"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        var members = await dbContext.SeriesCastRebuildMembers
            .FromSqlInterpolated($"""
                SELECT *
                FROM series_cast_rebuild_members
                WHERE "OwnerId" = {command.OwnerId}
                    AND "SeriesId" = {command.SeriesId}
                    AND "BatchId" = {command.BatchId}
                ORDER BY "Id"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        var revisions = await dbContext.NarrationCastRevisions
            .FromSqlInterpolated($"""
                SELECT *
                FROM narration_cast_revisions
                WHERE "OwnerId" = {command.OwnerId}
                    AND "SeriesId" = {command.SeriesId}
                ORDER BY "Id"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        ValidateBatchAndRevisions(series, batch, revisions);
        ValidateCohort(batch, seriesBooks, members);

        SeriesCastRebuildBatch? predecessorBatch = null;
        IReadOnlyList<SeriesCastRebuildMember> predecessorMembers = [];
        if (batch.BaseActiveCastRevisionId is Guid baseRevisionId)
        {
            var predecessorBatches = await dbContext.SeriesCastRebuildBatches
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM series_cast_rebuild_batches
                    WHERE "OwnerId" = {command.OwnerId}
                        AND "SeriesId" = {command.SeriesId}
                        AND "DraftCastRevisionId" = {baseRevisionId}
                        AND "Status" = 'Activated'
                    ORDER BY "Id"
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);
            if (predecessorBatches.Count != 1
                || predecessorBatches[0].Status != SeriesCastRebuildBatchStatus.Activated)
            {
                Reject(CastEpochActivationFailure.EpochStateInvalid);
            }

            predecessorBatch = predecessorBatches[0];
            predecessorMembers = await dbContext.SeriesCastRebuildMembers
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM series_cast_rebuild_members
                    WHERE "OwnerId" = {command.OwnerId}
                        AND "SeriesId" = {command.SeriesId}
                        AND "BatchId" = {predecessorBatch.Id}
                    ORDER BY "Id"
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);
        }

        var jobIds = members
            .SelectMany(member => new[]
            {
                member.StagedNarrationJobId,
                member.PreviousActiveNarrationJobId
            })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var jobs = jobIds.Length == 0
            ? []
            : await dbContext.NarrationJobs
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM narration_jobs
                    WHERE "Id" = ANY ({jobIds})
                    ORDER BY "Id"
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);

        var jobsById = jobs.ToDictionary(job => job.Id);
        ValidateArtifacts(
            command,
            series,
            batch,
            seriesBooks,
            members,
            predecessorBatch,
            predecessorMembers,
            jobsById);

        var draftRevision = revisions.Single(revision => revision.Id == batch.DraftCastRevisionId);
        var baseRevision = batch.BaseActiveCastRevisionId is Guid baseId
            ? revisions.Single(revision => revision.Id == baseId)
            : null;
        var nextEpoch = ValidateAndAllocateEpoch(revisions, baseRevision);
        ValidateActivationTime(command, series, batch, draftRevision, jobs);

        if (baseRevision is not null)
        {
            baseRevision.MakeHistorical();
        }

        foreach (var previousJob in members
                     .Where(member => member.PreviousActiveNarrationJobId.HasValue)
                     .Select(member => jobsById[member.PreviousActiveNarrationJobId!.Value])
                     .DistinctBy(job => job.Id))
        {
            previousJob.MarkHistorical(command.ActivatedAt);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        draftRevision.Activate(nextEpoch, command.ActivatedAt);
        foreach (var member in members)
        {
            jobsById[member.StagedNarrationJobId!.Value].Publish(command.ActivatedAt);
        }

        series.SwitchActiveCastRevision(
            batch.BaseActiveCastRevisionId,
            draftRevision.Id,
            command.ActivatedAt);
        var membersBySeriesBookId = members.ToDictionary(member => member.SeriesBookId);
        foreach (var seriesBook in seriesBooks)
        {
            var member = membersBySeriesBookId[seriesBook.Id];
            seriesBook.SwitchActiveNarrationJob(
                member.PreviousActiveNarrationJobId,
                member.StagedNarrationJobId!.Value);
        }

        batch.MarkActivated(command.ActivatedAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CastEpochActivationResult(
            series.Id,
            batch.Id,
            draftRevision.Id,
            nextEpoch,
            command.ActivatedAt);
    }

    private static void ValidateBatchAndRevisions(
        StorySeries series,
        SeriesCastRebuildBatch batch,
        IReadOnlyCollection<NarrationCastRevision> revisions)
    {
        if (batch.Status != SeriesCastRebuildBatchStatus.ReadyToActivate)
        {
            Reject(CastEpochActivationFailure.BatchNotReady);
        }

        if (series.ActiveCastRevisionId != batch.BaseActiveCastRevisionId)
        {
            Reject(CastEpochActivationFailure.StaleBaseRevision);
        }

        if (series.UpdatedAt > batch.CreatedAt)
        {
            Reject(CastEpochActivationFailure.StaleBaseRevision);
        }

        var draft = revisions.SingleOrDefault(revision => revision.Id == batch.DraftCastRevisionId);
        if (draft is null
            || draft.OwnerId != batch.OwnerId
            || draft.SeriesId != batch.SeriesId
            || draft.Status != NarrationCastRevisionStatus.Draft
            || draft.EpochNumber is not null
            || draft.ActivatedAt is not null)
        {
            Reject(CastEpochActivationFailure.DraftRevisionInvalid);
        }

        if (batch.BaseActiveCastRevisionId is null)
        {
            return;
        }

        var baseRevision = revisions.SingleOrDefault(
            revision => revision.Id == batch.BaseActiveCastRevisionId.Value);
        if (baseRevision is null
            || baseRevision.OwnerId != batch.OwnerId
            || baseRevision.SeriesId != batch.SeriesId
            || baseRevision.Status != NarrationCastRevisionStatus.Active
            || baseRevision.EpochNumber is not > 0
            || baseRevision.ActivatedAt is null
            || baseRevision.Id != series.ActiveCastRevisionId)
        {
            Reject(CastEpochActivationFailure.BaseRevisionInvalid);
        }
    }

    private static void ValidateCohort(
        SeriesCastRebuildBatch batch,
        IReadOnlyCollection<SeriesBook> seriesBooks,
        IReadOnlyCollection<SeriesCastRebuildMember> members)
    {
        if (seriesBooks.Count == 0 || members.Count == 0 || seriesBooks.Count != members.Count)
        {
            Reject(CastEpochActivationFailure.CohortChanged);
        }

        var membersBySeriesBookId = members
            .GroupBy(member => member.SeriesBookId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var seriesBook in seriesBooks)
        {
            if (!membersBySeriesBookId.TryGetValue(seriesBook.Id, out var matchingMembers)
                || matchingMembers.Length != 1
                || matchingMembers[0].BookId != seriesBook.BookId
                || matchingMembers[0].MembershipRevision != seriesBook.MembershipRevision)
            {
                Reject(CastEpochActivationFailure.CohortChanged);
            }
        }

        if (batch.CohortMembershipRevision != members.Max(member => member.MembershipRevision))
        {
            Reject(CastEpochActivationFailure.CohortChanged);
        }

        if (members.Any(member =>
                member.Status != SeriesCastRebuildMemberStatus.Ready
                || member.StagedNarrationJobId is null
                || member.StagedNarrationJobId == member.PreviousActiveNarrationJobId))
        {
            Reject(CastEpochActivationFailure.MemberInvalid);
        }
    }

    private static void ValidateArtifacts(
        CastEpochActivationCommand command,
        StorySeries series,
        SeriesCastRebuildBatch batch,
        IReadOnlyCollection<SeriesBook> seriesBooks,
        IReadOnlyCollection<SeriesCastRebuildMember> members,
        SeriesCastRebuildBatch? predecessorBatch,
        IReadOnlyCollection<SeriesCastRebuildMember> predecessorMembers,
        IReadOnlyDictionary<Guid, NarrationJob> jobsById)
    {
        var seriesBooksById = seriesBooks.ToDictionary(seriesBook => seriesBook.Id);
        var predecessorMembersBySeriesBookId = predecessorMembers
            .GroupBy(member => member.SeriesBookId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var member in members)
        {
            if (!jobsById.TryGetValue(member.StagedNarrationJobId!.Value, out var stagedJob)
                || stagedJob.OwnerId != command.OwnerId
                || stagedJob.SeriesId != command.SeriesId
                || stagedJob.RebuildBatchId != command.BatchId
                || stagedJob.RebuildMemberId != member.Id
                || stagedJob.BookId != member.BookId
                || stagedJob.CastRevisionId != batch.DraftCastRevisionId
                || stagedJob.Mode != NarrationMode.MultiCharacter
                || stagedJob.Status != NarrationJobStatus.Completed
                || stagedJob.Visibility != NarrationArtifactVisibility.Staged
                || !stagedJob.HasSafeCompletedAudio)
            {
                Reject(CastEpochActivationFailure.StagedArtifactInvalid);
            }

            var seriesBook = seriesBooksById[member.SeriesBookId];
            if (member.PreviousActiveNarrationJobId != seriesBook.ActiveNarrationJobId)
            {
                Reject(CastEpochActivationFailure.PreviousPointerChanged);
            }

            if (member.PreviousActiveNarrationJobId is not Guid previousJobId)
            {
                if (batch.BaseActiveCastRevisionId is not null)
                {
                    Reject(CastEpochActivationFailure.PreviousArtifactInvalid);
                }

                continue;
            }

            if (!jobsById.TryGetValue(previousJobId, out var previousJob)
                || previousJob.OwnerId != command.OwnerId
                || previousJob.BookId != member.BookId
                || previousJob.Status != NarrationJobStatus.Completed
                || previousJob.Visibility != NarrationArtifactVisibility.Published
                || !previousJob.HasSafeCompletedAudio)
            {
                Reject(CastEpochActivationFailure.PreviousArtifactInvalid);
            }

            if (batch.BaseActiveCastRevisionId is null)
            {
                if (previousJob.Mode != NarrationMode.SingleVoice
                    || previousJob.SeriesId is not null
                    || previousJob.CastRevisionId is not null
                    || previousJob.SpeechPlanRevisionId is not null
                    || previousJob.RebuildBatchId is not null
                    || previousJob.RebuildMemberId is not null)
                {
                    Reject(CastEpochActivationFailure.PreviousArtifactInvalid);
                }

                continue;
            }

            if (predecessorBatch is null
                || previousJob.Mode != NarrationMode.MultiCharacter
                || previousJob.SeriesId != series.Id
                || previousJob.CastRevisionId != batch.BaseActiveCastRevisionId
                || previousJob.RebuildBatchId != predecessorBatch.Id
                || !predecessorMembersBySeriesBookId.TryGetValue(
                    member.SeriesBookId,
                    out var matchingPredecessors)
                || matchingPredecessors.Length != 1
                || matchingPredecessors[0].BookId != member.BookId
                || matchingPredecessors[0].StagedNarrationJobId != previousJob.Id
                || previousJob.RebuildMemberId != matchingPredecessors[0].Id)
            {
                Reject(CastEpochActivationFailure.PreviousArtifactInvalid);
            }
        }
    }

    private static int ValidateAndAllocateEpoch(
        IReadOnlyCollection<NarrationCastRevision> revisions,
        NarrationCastRevision? baseRevision)
    {
        var epochRevisions = revisions
            .Where(revision => revision.EpochNumber.HasValue)
            .OrderBy(revision => revision.EpochNumber)
            .ToArray();
        if (baseRevision is null)
        {
            if (epochRevisions.Length != 0
                || revisions.Any(revision =>
                    revision.Status is NarrationCastRevisionStatus.Active
                        or NarrationCastRevisionStatus.Historical))
            {
                Reject(CastEpochActivationFailure.EpochStateInvalid);
            }

            return 1;
        }

        if (epochRevisions.Length == 0
            || epochRevisions.Count(revision => revision.Status == NarrationCastRevisionStatus.Active) != 1
            || epochRevisions.Single(revision => revision.Status == NarrationCastRevisionStatus.Active).Id
                != baseRevision.Id
            || epochRevisions.Any(revision =>
                revision.ActivatedAt is null
                || revision.Status == NarrationCastRevisionStatus.Draft)
            || epochRevisions.Select(revision => revision.EpochNumber!.Value)
                .Distinct()
                .Count() != epochRevisions.Length)
        {
            Reject(CastEpochActivationFailure.EpochStateInvalid);
        }

        var maximumEpoch = epochRevisions.Max(revision => revision.EpochNumber!.Value);
        if (baseRevision.EpochNumber != maximumEpoch
            || epochRevisions.Length != maximumEpoch
            || !epochRevisions.Select(revision => revision.EpochNumber!.Value)
                .SequenceEqual(Enumerable.Range(1, maximumEpoch)))
        {
            Reject(CastEpochActivationFailure.EpochStateInvalid);
        }

        try
        {
            return checked(maximumEpoch + 1);
        }
        catch (OverflowException)
        {
            Reject(CastEpochActivationFailure.EpochStateInvalid);
            throw;
        }
    }

    private static void ValidateActivationTime(
        CastEpochActivationCommand command,
        StorySeries series,
        SeriesCastRebuildBatch batch,
        NarrationCastRevision draft,
        IReadOnlyCollection<NarrationJob> jobs)
    {
        if (command.ActivatedAt < series.UpdatedAt
            || command.ActivatedAt < batch.UpdatedAt
            || command.ActivatedAt < draft.CreatedAt
            || jobs.Any(job => command.ActivatedAt < job.UpdatedAt))
        {
            Reject(CastEpochActivationFailure.EpochStateInvalid);
        }
    }

    private static bool IsRetryablePostgreSqlFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException postgresException
                && postgresException.SqlState is PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected)
            {
                return true;
            }
        }

        return false;
    }

    [DoesNotReturn]
    private static void Reject(CastEpochActivationFailure failure) =>
        throw new CastEpochActivationRejectedException(failure);
}

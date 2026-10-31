using System.Data;
using Microsoft.EntityFrameworkCore;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

/// <summary>
/// Applies a terminal staged job's durable result to its rebuild cohort. This is intentionally
/// idempotent so lease recovery and an ambiguous worker completion can safely replay it.
/// </summary>
internal sealed class StagedNarrationBatchProgressService(StoryVoiceDbContext dbContext)
    : IStagedNarrationBatchProgressService
{
    public async Task SynchronizeAsync(Guid narrationJobId, CancellationToken cancellationToken)
    {
        if (narrationJobId == Guid.Empty)
        {
            throw new ArgumentException("朗讀工作識別碼不可為空白。", nameof(narrationJobId));
        }

        var job = await dbContext.NarrationJobs
            .AsNoTracking()
            .Where(candidate => candidate.Id == narrationJobId
                && candidate.Mode == NarrationMode.MultiCharacter
                && candidate.Visibility == NarrationArtifactVisibility.Staged
                && candidate.RebuildBatchId != null
                && candidate.RebuildMemberId != null)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.OwnerId,
                candidate.SeriesId,
                candidate.RebuildBatchId,
                candidate.RebuildMemberId,
                candidate.Status
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (job is null
            || job.SeriesId is null
            || job.RebuildBatchId is null
            || job.RebuildMemberId is null
            || job.Status is not (NarrationJobStatus.Completed or NarrationJobStatus.Failed or NarrationJobStatus.Cancelled))
        {
            return;
        }

        var usesPostgresBatchLock = dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        await using var transaction = usesPostgresBatchLock
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        if (usesPostgresBatchLock)
        {
            // One batch row serializes concurrent terminal callbacks without widening the lock
            // to every narration job. The subsequent replay reconciliation remains necessary
            // for interrupted callbacks and non-PostgreSQL test providers.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT \"Id\" FROM \"series_cast_rebuild_batches\" WHERE \"Id\" = {job.RebuildBatchId} FOR UPDATE",
                cancellationToken);
        }

        var batch = await dbContext.SeriesCastRebuildBatches
            .Include(candidate => candidate.Members)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == job.RebuildBatchId
                    && candidate.OwnerId == job.OwnerId
                    && candidate.SeriesId == job.SeriesId,
                cancellationToken);
        if (batch is null || batch.Status != SeriesCastRebuildBatchStatus.Building)
        {
            return;
        }

        var member = batch.Members.SingleOrDefault(candidate => candidate.Id == job.RebuildMemberId);
        if (member is null || member.StagedNarrationJobId != job.Id)
        {
            return;
        }

        // A recovery can requeue this job while an older terminal callback waits for the batch
        // lock. Re-read after acquiring that lock so the stale failure cannot fail it again.
        var currentStatus = await dbContext.NarrationJobs.AsNoTracking()
            .Where(candidate => candidate.Id == job.Id && candidate.RebuildBatchId == batch.Id)
            .Select(candidate => (NarrationJobStatus?)candidate.Status).SingleOrDefaultAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        switch (currentStatus)
        {
            case NarrationJobStatus.Completed when member.Status == SeriesCastRebuildMemberStatus.Building:
                batch.MarkMemberReady(member.SeriesBookId, now);
                changed = true;
                break;
            case NarrationJobStatus.Failed or NarrationJobStatus.Cancelled
                when member.Status is SeriesCastRebuildMemberStatus.Pending or SeriesCastRebuildMemberStatus.Building:
                batch.MarkMemberFailed(member.SeriesBookId, now);
                changed = true;
                break;
        }

        changed |= batch.ReconcileTerminalMemberState(now);
        if (!changed)
        {
            return;
        }

        // 批次一旦 Failed，其餘成員的 staged jobs 不該繼續消耗 GPU／付費 API：
        // 佇列中的立即取消，執行中的標記取消請求（worker 監看後會中止 provider）。
        // 這些產物本來就注定在下一次 purge 被整批刪除。
        if (batch.Status == SeriesCastRebuildBatchStatus.Failed)
        {
            var siblingJobs = await dbContext.NarrationJobs
                .Where(candidate => candidate.RebuildBatchId == batch.Id
                    && candidate.Id != job.Id
                    && candidate.Visibility == NarrationArtifactVisibility.Staged
                    && (candidate.Status == NarrationJobStatus.Queued
                        || candidate.Status == NarrationJobStatus.Running))
                .ToListAsync(cancellationToken);
            foreach (var siblingJob in siblingJobs)
            {
                siblingJob.RequestCancellation();
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }
}

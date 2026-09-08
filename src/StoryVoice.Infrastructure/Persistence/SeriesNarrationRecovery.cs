using System.Data;
using Microsoft.EntityFrameworkCore;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Narrations;
using StoryVoice.Domain.Series;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed partial class SeriesNarrationService
{
    public async Task<IReadOnlyList<SeriesNarrationRebuildResponse>?> ListRebuildsAsync(
        Guid seriesId, CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        if (!await dbContext.StorySeries.AnyAsync(series => series.Id == seriesId && series.OwnerId == ownerId, cancellationToken))
            return null;
        var batches = await dbContext.SeriesCastRebuildBatches.AsNoTracking().AsSplitQuery()
            .Include(batch => batch.Members)
            .Where(batch => batch.SeriesId == seriesId && batch.OwnerId == ownerId)
            .OrderByDescending(batch => batch.CreatedAt).ThenByDescending(batch => batch.Id)
            .Take(20).ToListAsync(cancellationToken);
        return batches.Select(ToResponse).ToArray();
    }

    public async Task<SeriesNarrationRebuildResponse?> RetryRebuildAsync(
        Guid seriesId, Guid batchId, CreateSeriesNarrationRebuildRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!admissionOptions.Value.AdmissionEnabled) throw new NarrationAdmissionDisabledException();
        if (!request.RightsAttested) throw new NarrationRightsRequiredException();
        var ownerId = EnsureCurrentOwnerId();
        var postgres = dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        await using var transaction = await BeginTransactionAsync(cancellationToken, IsolationLevel.ReadCommitted);

        // Activation and discard use the same series -> batch -> jobs lock order.
        var series = await (postgres
            ? dbContext.StorySeries.FromSqlInterpolated($"SELECT * FROM story_series WHERE \"Id\" = {seriesId} AND \"OwnerId\" = {ownerId} FOR UPDATE")
            : dbContext.StorySeries.Where(series => series.Id == seriesId && series.OwnerId == ownerId))
            .SingleOrDefaultAsync(cancellationToken);
        if (series is null) return null;
        var batch = await (postgres
            ? dbContext.SeriesCastRebuildBatches.FromSqlInterpolated($"SELECT * FROM series_cast_rebuild_batches WHERE \"Id\" = {batchId} AND \"SeriesId\" = {seriesId} AND \"OwnerId\" = {ownerId} FOR UPDATE")
            : dbContext.SeriesCastRebuildBatches.Where(batch => batch.Id == batchId && batch.SeriesId == seriesId && batch.OwnerId == ownerId))
            .SingleOrDefaultAsync(cancellationToken);
        if (batch is null) return null;
        await dbContext.Entry(batch).Collection(batch => batch.Members).LoadAsync(cancellationToken);
        var cast = await dbContext.NarrationCastRevisions.Include(cast => cast.Assignments)
            .SingleAsync(cast => cast.Id == batch.DraftCastRevisionId && cast.OwnerId == ownerId && cast.SeriesId == seriesId, cancellationToken);
        if (cast.NarratorProvider != CharacterVoiceProviders.BlueMagpie
            || !blueMagpieOptions.Value.Enabled || !blueMagpieOptions.Value.FormalNarrationEnabled)
            throw new InvalidOperationException("目前只支援已啟用正式配音的 BlueMagpie 批次恢復。");
        if (batch.Status == SeriesCastRebuildBatchStatus.Building) return ToResponse(batch);
        if (batch.Status != SeriesCastRebuildBatchStatus.Failed || cast.Status != NarrationCastRevisionStatus.Draft)
            throw new InvalidOperationException("只有失敗且尚未啟用的批次可以恢復。");
        if (await dbContext.SeriesCastRebuildBatches.AnyAsync(other => other.OwnerId == ownerId && other.SeriesId == seriesId
            && other.Id != batchId && other.Status != SeriesCastRebuildBatchStatus.Failed
            && other.Status != SeriesCastRebuildBatchStatus.Activated, cancellationToken))
            throw new InvalidOperationException("這個系列已有另一個待完成批次，無法恢復舊批次。");

        await dbContext.Entry(series).Collection(series => series.Books).LoadAsync(cancellationToken);
        await dbContext.Entry(series).Collection(series => series.Characters).LoadAsync(cancellationToken);
        var memberships = series.Books.ToArray();
        if (series.ActiveCastRevisionId != batch.BaseActiveCastRevisionId || memberships.Length != batch.Members.Count
            || batch.Members.Any(member => !memberships.Any(current => current.Id == member.SeriesBookId
                && current.BookId == member.BookId && current.MembershipRevision == member.MembershipRevision
                && current.ActiveNarrationJobId == member.PreviousActiveNarrationJobId))
            || !MatchesRecoveryCast(series, cast))
            throw new InvalidOperationException("系列成員、角色聲線或合成設定已變更，請建立新批次。");

        var jobs = await (postgres
            ? dbContext.NarrationJobs.FromSqlInterpolated($"SELECT * FROM narration_jobs WHERE \"OwnerId\" = {ownerId} AND \"SeriesId\" = {seriesId} AND \"RebuildBatchId\" = {batchId} ORDER BY \"Id\" FOR UPDATE")
            : dbContext.NarrationJobs.Where(job => job.OwnerId == ownerId && job.SeriesId == seriesId && job.RebuildBatchId == batchId))
            .ToListAsync(cancellationToken);
        if (jobs.Count != batch.Members.Count || jobs.Any(job => job.Visibility != NarrationArtifactVisibility.Staged
            || job.Mode != NarrationMode.MultiCharacter || job.CastRevisionId != cast.Id
            || !batch.Members.Any(member => member.StagedNarrationJobId == job.Id && member.Id == job.RebuildMemberId && member.BookId == job.BookId)))
            throw new InvalidOperationException("批次工作資料不完整，請建立新批次。");
        if (jobs.Any(job => job.Status is NarrationJobStatus.Queued or NarrationJobStatus.Running))
            throw new InvalidOperationException("請等批次內所有工作停止後再恢復。");
        if (jobs.Any(job => job.Status == NarrationJobStatus.Completed && !job.HasSafeCompletedAudio))
            throw new InvalidOperationException("已完成工作的音訊資料不完整，請建立新批次。");
        var failed = jobs.Where(job => job.Status == NarrationJobStatus.Failed).ToArray();
        if (failed.Length == 0 || failed.Any(job => job.ErrorCode is not ("provider_failed" or "provider_timeout" or "worker_lease_expired")))
            throw new InvalidOperationException("這個批次沒有可恢復的暫時性失敗；請修正問題後建立新批次。");

        var books = await LoadSeriesBooksAsync(ownerId, memberships, cancellationToken);
        var plans = await LoadLatestConfirmedPlansAsync(ownerId, seriesId, memberships.Select(member => member.BookId).ToArray(), cancellationToken);
        var snapshots = BuildSourceAndPlanSnapshots(memberships, books, plans, series.NarrativeVoiceMode, cancellationToken);
        EnsureBlueMagpieJobAdmissionBudget(snapshots, blueMagpieOptions.Value);
        var jobIds = jobs.Select(job => job.Id).ToArray();
        var links = await dbContext.NarrationJobSpeechPlans.AsNoTracking()
            .Where(link => link.OwnerId == ownerId && link.SeriesId == seriesId && jobIds.Contains(link.NarrationJobId))
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            var snapshot = snapshots[job.BookId];
            var expectedPlans = snapshot.Plans.Select(plan => (plan.ChapterSortOrder, plan.Revision.Id)).OrderBy(plan => plan.ChapterSortOrder);
            var lockedPlans = links.Where(link => link.NarrationJobId == job.Id)
                .Select(link => (link.ChapterSortOrder, link.ConfirmedSpeechPlanRevisionId)).OrderBy(plan => plan.ChapterSortOrder);
            if (job.ContentBookId != job.BookId || job.SourceHash != snapshot.Source.SourceHash || !expectedPlans.SequenceEqual(lockedPlans))
                throw new InvalidOperationException("書籍正文或已確認劇本已變更，請建立新批次。");
        }

        var now = DateTimeOffset.UtcNow;
        var completed = jobs.Where(job => job.Status == NarrationJobStatus.Completed).Select(job => job.Id).ToHashSet();
        foreach (var job in jobs.Where(job => job.Status != NarrationJobStatus.Completed)) job.Requeue(now);
        batch.ResumeFailed(completed, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return ToResponse(batch);
    }

    private bool MatchesRecoveryCast(StorySeries series, NarrationCastRevision cast)
    {
        var configuration = compositionOptions.Value;
        return cast.VerifyFingerprint()
            && cast.NarratorProvider == series.NarratorProvider && cast.NarratorVoice == series.NarratorVoice
            && cast.NarratorRate == series.NarratorRate && cast.NarratorPitch == series.NarratorPitch && cast.NarratorVolume == series.NarratorVolume
            && cast.DefaultSpeakerPauseMs == series.DefaultSpeakerPauseMs && cast.ChapterPauseMs == configuration.ChapterPauseMs
            && cast.NarratorProviderVersion == configuration.ResolveProviderVersion(series.NarratorProvider)
            && cast.CompositionVersion == BuildEffectiveCompositionVersion(configuration.ResolveCompositionVersion(series.NarratorProvider), series.NarrativeVoiceMode, series.PointOfViewCharacterId)
            && cast.FfmpegProfile == configuration.ResolveFfmpegProfile(series.NarratorProvider)
            && cast.Assignments.Count == series.Characters.Count
            && cast.Assignments.All(assignment => series.Characters.Any(character => character.Id == assignment.CharacterId
                && character.CanonicalName == assignment.CanonicalNameSnapshot && character.VoiceProvider == assignment.VoiceProvider
                && assignment.ProviderVersion == configuration.ResolveProviderVersion(character.VoiceProvider)
                && character.Voice == assignment.Voice && character.Rate == assignment.Rate
                && character.Pitch == assignment.Pitch && character.Volume == assignment.Volume));
    }
}

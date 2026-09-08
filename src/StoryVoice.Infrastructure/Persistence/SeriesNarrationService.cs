using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Books;
using StoryVoice.Application.Narrations;
using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Domain.Series;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

/// <summary>
/// Creates immutable, staged series-wide multi-character narration cohorts. It deliberately never
/// changes active playback pointers; only PostgreSqlCastEpochActivationPublisher can publish a
/// completed cohort.
/// </summary>
internal sealed partial class SeriesNarrationService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser,
    ChineseSpeechSegmenter segmenter,
    IOptions<NarrationAdmissionOptions> admissionOptions,
    IOptions<MultiCharacterNarrationOptions> compositionOptions,
    IOptions<BlueMagpieOptions> blueMagpieOptions,
    IOptions<NarrationOptions> narrationOptions,
    PostgreSqlCastEpochActivationPublisher activationPublisher) : ISeriesNarrationService
{
    public async Task<SeriesNarrationRebuildResponse?> CreateRebuildAsync(
        Guid seriesId,
        CreateSeriesNarrationRebuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!admissionOptions.Value.AdmissionEnabled)
        {
            throw new NarrationAdmissionDisabledException();
        }

        if (!request.RightsAttested)
        {
            throw new NarrationRightsRequiredException();
        }

        EnsureId(seriesId, nameof(seriesId));
        var ownerId = EnsureCurrentOwnerId();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            var series = await dbContext.StorySeries
                .AsSplitQuery()
                .Include(candidate => candidate.Books)
                .Include(candidate => candidate.Characters)
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == seriesId && candidate.OwnerId == ownerId,
                    cancellationToken);
            if (series is null)
            {
                return null;
            }

            var isBlueMagpie = string.Equals(
                series.NarratorProvider,
                CharacterVoiceProviders.BlueMagpie,
                StringComparison.OrdinalIgnoreCase);
            if (isBlueMagpie && !blueMagpieOptions.Value.FormalNarrationEnabled)
            {
                throw new InvalidOperationException(
                    "BlueMagpie 目前只開放固定句試音；正式小說配音尚未由管理員啟用。");
            }

            var isThreeWa = string.Equals(
                series.NarratorProvider,
                CharacterVoiceProviders.ThreeWaVoxCpm2,
                StringComparison.OrdinalIgnoreCase);
            if (isThreeWa && !ThreeWaSynthesisCapabilities.SupportsTrustedCloneFormalNarration)
            {
                throw new InvalidOperationException(
                    ThreeWaSynthesisCapabilities.CloneFormalNarrationUnavailableMessage);
            }

            var memberships = series.Books
                .OrderBy(member => member.SortOrder)
                .ThenBy(member => member.Id)
                .ToArray();
            if (memberships.Length == 0)
            {
                throw new InvalidOperationException("系列至少要先加入一本含合法正文的書，才能建立多聲線朗讀批次。");
            }

            if (series.Characters.Count == 0)
            {
                throw new InvalidOperationException("系列至少要先設定一位角色與固定聲線，才能建立多聲線朗讀批次。");
            }

            if (series.NarrativeVoiceMode == NarrativeVoiceMode.PointOfViewInnerMonologue
                && (series.PointOfViewCharacterId is not Guid pointOfViewCharacterId
                    || series.Characters.All(character => character.Id != pointOfViewCharacterId)))
            {
                throw new InvalidOperationException("主角視角敘述模式必須指定系列內有效的視角角色。");
            }

            if (await dbContext.SeriesCastRebuildBatches.AnyAsync(
                    batch => batch.OwnerId == ownerId
                        && batch.SeriesId == seriesId
                        && (batch.Status == SeriesCastRebuildBatchStatus.Draft
                            || batch.Status == SeriesCastRebuildBatchStatus.Building
                            || batch.Status == SeriesCastRebuildBatchStatus.ReadyToActivate),
                    cancellationToken))
            {
                throw new InvalidOperationException("這個系列已有尚未完成啟用的多聲線重建批次。");
            }

            EnsureSingleSynthesisProvider(series);
            await EnsureThreeWaCharacterProfilesAvailableAsync(ownerId, series, cancellationToken);
            var sourceBooks = await LoadSeriesBooksAsync(ownerId, memberships, cancellationToken);
            var confirmedPlans = await LoadLatestConfirmedPlansAsync(
                ownerId,
                seriesId,
                memberships.Select(member => member.BookId).ToArray(),
                cancellationToken);
            var sourceAndPlans = BuildSourceAndPlanSnapshots(
                memberships,
                sourceBooks,
                confirmedPlans,
                series.NarrativeVoiceMode,
                cancellationToken);

            if (isBlueMagpie)
            {
                EnsureBlueMagpieJobAdmissionBudget(sourceAndPlans, blueMagpieOptions.Value);
            }

            // Budget preflight above must be read-only: an oversized retry cannot purge its old
            // failed batch (or make any other database mutation). Once admission succeeds, a batch
            // and its draft cast revision are each usable exactly once
            // (UX_rebuild_batches_draft_cast / UX_ncast_revs_fingerprint). A failed batch can then
            // be safely removed before staging the fresh attempt.
            await PurgeFailedBatchesAsync(ownerId, seriesId, cancellationToken);

            var configuration = compositionOptions.Value;
            ValidateComposition(configuration, series);
            var narratorProviderVersion = configuration.ResolveProviderVersion(series.NarratorProvider);
            var compositionVersion = configuration.ResolveCompositionVersion(series.NarratorProvider);
            var ffmpegProfile = configuration.ResolveFfmpegProfile(series.NarratorProvider);
            var effectiveCompositionVersion = BuildEffectiveCompositionVersion(
                compositionVersion,
                series.NarrativeVoiceMode,
                series.PointOfViewCharacterId);
            var now = DateTimeOffset.UtcNow;
            var tentativeCastRevisionId = Guid.NewGuid();
            var tentativeAssignments = series.Characters
                .OrderBy(character => character.Id)
                .Select(character => NarrationCastAssignment.Create(
                    Guid.NewGuid(),
                    ownerId,
                    seriesId,
                    tentativeCastRevisionId,
                    character.Id,
                    character.CanonicalName,
                    character.VoiceProvider,
                    configuration.ResolveProviderVersion(character.VoiceProvider),
                    character.Voice,
                    character.Rate,
                    character.Pitch,
                    character.Volume))
                .ToArray();
            var canonicalAssignments = tentativeAssignments
                .OrderBy(
                    assignment => assignment.CharacterId.ToString("N", CultureInfo.InvariantCulture),
                    StringComparer.Ordinal)
                .ToArray();
            var prospectiveFingerprint = NarrationCastRevision.ComputeFingerprint(
                series.NarratorProvider,
                narratorProviderVersion,
                series.NarratorVoice,
                series.NarratorRate,
                series.NarratorPitch,
                series.NarratorVolume,
                series.DefaultSpeakerPauseMs,
                configuration.ChapterPauseMs,
                effectiveCompositionVersion,
                ffmpegProfile,
                canonicalAssignments);

            // A voice cast that hasn't changed since a previous (e.g. failed) attempt hashes to
            // the same fingerprint; UX_ncast_revs_fingerprint would reject a second insert. Reuse
            // that still-unused draft instead of minting a duplicate.
            var reusableRevisionId = await dbContext.NarrationCastRevisions
                .Where(revision => revision.OwnerId == ownerId
                    && revision.SeriesId == seriesId
                    && revision.Fingerprint == prospectiveFingerprint
                    && revision.Status == NarrationCastRevisionStatus.Draft)
                .Select(revision => (Guid?)revision.Id)
                .FirstOrDefaultAsync(cancellationToken);

            Guid castRevisionId;
            if (reusableRevisionId is Guid existingRevisionId)
            {
                castRevisionId = existingRevisionId;
            }
            else
            {
                castRevisionId = tentativeCastRevisionId;
                var nextCastRevisionNumber = await dbContext.NarrationCastRevisions
                    .Where(revision => revision.OwnerId == ownerId && revision.SeriesId == seriesId)
                    .Select(revision => revision.RevisionNumber)
                    .OrderByDescending(revisionNumber => revisionNumber)
                    .FirstOrDefaultAsync(cancellationToken) + 1;
                var castRevision = NarrationCastRevision.Create(
                    castRevisionId,
                    ownerId,
                    seriesId,
                    nextCastRevisionNumber,
                    series.NarratorProvider,
                    narratorProviderVersion,
                    series.NarratorVoice,
                    series.NarratorRate,
                    series.NarratorPitch,
                    series.NarratorVolume,
                    series.DefaultSpeakerPauseMs,
                    configuration.ChapterPauseMs,
                    effectiveCompositionVersion,
                    ffmpegProfile,
                    now,
                    tentativeAssignments);
                dbContext.NarrationCastRevisions.Add(castRevision);
            }

            var batchId = Guid.NewGuid();
            var batch = SeriesCastRebuildBatch.Create(
                batchId,
                ownerId,
                seriesId,
                series.ActiveCastRevisionId,
                castRevisionId,
                memberships.Max(member => member.MembershipRevision),
                now,
                memberships.Select(member => SeriesCastRebuildMember.Create(
                    Guid.NewGuid(),
                    ownerId,
                    seriesId,
                    batchId,
                    member.Id,
                    member.BookId,
                    member.MembershipRevision,
                    member.ActiveNarrationJobId)));
            batch.StartBuilding(now);

            var stagedJobs = new List<NarrationJob>(memberships.Length);
            var planLinks = new List<NarrationJobSpeechPlan>();
            foreach (var membership in memberships)
            {
                var snapshot = sourceAndPlans[membership.BookId];
                var rebuildMember = batch.Members.Single(member => member.SeriesBookId == membership.Id);
                var primaryPlanId = snapshot.Plans
                    .OrderBy(plan => plan.ChapterSortOrder)
                    .ThenBy(plan => plan.Revision.Id)
                    .First()
                    .Revision.Id;
                var stagedJob = NarrationJob.CreateMultiCharacterStaged(
                    ownerId,
                    membership.BookId,
                    membership.BookId,
                    seriesId,
                    castRevisionId,
                    primaryPlanId,
                    batchId,
                    rebuildMember.Id,
                    snapshot.Source.SourceHash,
                    now);
                batch.AttachStagedJob(membership.Id, stagedJob.Id);
                stagedJobs.Add(stagedJob);
                planLinks.AddRange(snapshot.Plans.Select(plan => NarrationJobSpeechPlan.Create(
                    ownerId,
                    seriesId,
                    stagedJob.Id,
                    plan.ChapterSortOrder,
                    plan.Revision.Id)));
            }

            dbContext.SeriesCastRebuildBatches.Add(batch);
            dbContext.NarrationJobs.AddRange(stagedJobs);
            dbContext.NarrationJobSpeechPlans.AddRange(planLinks);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return ToResponse(batch);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<SeriesNarrationRebuildResponse?> GetRebuildAsync(
        Guid seriesId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        EnsureId(batchId, nameof(batchId));
        var ownerId = EnsureCurrentOwnerId();
        var batch = await dbContext.SeriesCastRebuildBatches
            .AsNoTracking()
            .AsSplitQuery()
            .Include(candidate => candidate.Members)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == batchId
                    && candidate.SeriesId == seriesId
                    && candidate.OwnerId == ownerId,
                cancellationToken);
        return batch is null ? null : ToResponse(batch);
    }

    public async Task<SeriesNarrationRebuildResponse?> DiscardRebuildAsync(
        Guid seriesId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        EnsureId(batchId, nameof(batchId));
        var ownerId = EnsureCurrentOwnerId();
        var usesPostgresRowLocks = dbContext.Database.ProviderName
            == "Npgsql.EntityFrameworkCore.PostgreSQL";
        await using var transaction = await BeginTransactionAsync(
            cancellationToken,
            IsolationLevel.ReadCommitted);
        try
        {
            SeriesCastRebuildBatch? batch;
            if (usesPostgresRowLocks)
            {
                // Match activation's series -> batch lock order. Whichever transition wins the
                // series lock is durable before the other reads the batch, so a discarded batch
                // can never race back into Activated and an Activated batch is never rewritten.
                var series = await dbContext.StorySeries
                    .FromSqlInterpolated($"""
                        SELECT *
                        FROM story_series
                        WHERE "OwnerId" = {ownerId}
                            AND "Id" = {seriesId}
                        FOR UPDATE
                        """)
                    .SingleOrDefaultAsync(cancellationToken);
                if (series is null)
                {
                    return null;
                }

                batch = await dbContext.SeriesCastRebuildBatches
                    .FromSqlInterpolated($"""
                        SELECT *
                        FROM series_cast_rebuild_batches
                        WHERE "OwnerId" = {ownerId}
                            AND "SeriesId" = {seriesId}
                            AND "Id" = {batchId}
                        FOR UPDATE
                        """)
                    .SingleOrDefaultAsync(cancellationToken);
                if (batch is not null)
                {
                    await dbContext.Entry(batch)
                        .Collection(candidate => candidate.Members)
                        .LoadAsync(cancellationToken);
                }
            }
            else
            {
                batch = await dbContext.SeriesCastRebuildBatches
                    .AsSplitQuery()
                    .Include(candidate => candidate.Members)
                    .SingleOrDefaultAsync(
                        candidate => candidate.Id == batchId
                            && candidate.SeriesId == seriesId
                            && candidate.OwnerId == ownerId,
                        cancellationToken);
            }

            if (batch is null)
            {
                return null;
            }

            if (batch.Status == SeriesCastRebuildBatchStatus.Activated)
            {
                throw new InvalidOperationException("已啟用的多聲線重建批次不可丟棄。");
            }

            var stagedJobs = usesPostgresRowLocks
                ? await dbContext.NarrationJobs
                    .FromSqlInterpolated($"""
                        SELECT *
                        FROM narration_jobs
                        WHERE "OwnerId" = {ownerId}
                            AND "SeriesId" = {seriesId}
                            AND "RebuildBatchId" = {batchId}
                            AND "Mode" = 'MultiCharacter'
                            AND "Visibility" = 'Staged'
                        ORDER BY "Id"
                        FOR UPDATE
                        """)
                    .ToListAsync(cancellationToken)
                : await dbContext.NarrationJobs
                    .Where(job => job.OwnerId == ownerId
                        && job.SeriesId == seriesId
                        && job.RebuildBatchId == batchId
                        && job.Mode == NarrationMode.MultiCharacter
                        && job.Visibility == NarrationArtifactVisibility.Staged)
                    .OrderBy(job => job.Id)
                    .ToListAsync(cancellationToken);

            foreach (var job in stagedJobs.Where(job =>
                         job.Status is NarrationJobStatus.Queued or NarrationJobStatus.Running))
            {
                job.RequestCancellation();
            }

            if (batch.Status != SeriesCastRebuildBatchStatus.Failed)
            {
                batch.Invalidate(DateTimeOffset.UtcNow);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return ToResponse(batch);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<SeriesNarrationRebuildResponse?> ActivateRebuildAsync(
        Guid seriesId,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        EnsureId(seriesId, nameof(seriesId));
        EnsureId(batchId, nameof(batchId));
        var ownerId = EnsureCurrentOwnerId();
        var exists = await dbContext.SeriesCastRebuildBatches
            .AsNoTracking()
            .AnyAsync(
                batch => batch.Id == batchId
                    && batch.SeriesId == seriesId
                    && batch.OwnerId == ownerId,
                cancellationToken);
        if (!exists)
        {
            return null;
        }

        await activationPublisher.ActivateAsync(
            new CastEpochActivationCommand(ownerId, seriesId, batchId, DateTimeOffset.UtcNow),
            cancellationToken);
        return await GetRebuildAsync(seriesId, batchId, cancellationToken);
    }

    /// <summary>
    /// Removes batches that already failed for this series, along with the rows that only exist
    /// to serve them (their staged jobs, plan links, members, and — when nothing else references
    /// it — their draft cast revision). Never touches a job whose <see cref="NarrationArtifactVisibility"/>
    /// is <c>Published</c>, and never touches a cast revision that isn't still <c>Draft</c>.
    /// </summary>
    private async Task PurgeFailedBatchesAsync(Guid ownerId, Guid seriesId, CancellationToken cancellationToken)
    {
        var failedBatches = await dbContext.SeriesCastRebuildBatches
            .Include(batch => batch.Members)
            .Where(batch => batch.OwnerId == ownerId
                && batch.SeriesId == seriesId
                && batch.Status == SeriesCastRebuildBatchStatus.Failed)
            .ToArrayAsync(cancellationToken);
        if (failedBatches.Length == 0)
        {
            return;
        }

        var batchIds = failedBatches.Select(batch => batch.Id).ToArray();
        // 還在 Running 的 staged job 不可以被硬刪（合成中的資料列被另一個交易憑空
        // 移除只是「靠巧合安全」）；整個 batch 先跳過，等取消落地後下一輪再清。
        var runningBatchIds = await dbContext.NarrationJobs
            .Where(job => job.OwnerId == ownerId
                && job.SeriesId == seriesId
                && job.RebuildBatchId != null
                && batchIds.Contains(job.RebuildBatchId!.Value)
                && job.Visibility == NarrationArtifactVisibility.Staged
                && job.Status == NarrationJobStatus.Running)
            .Select(job => job.RebuildBatchId!.Value)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        failedBatches = failedBatches
            .Where(batch => !runningBatchIds.Contains(batch.Id))
            .ToArray();
        if (failedBatches.Length == 0)
        {
            return;
        }

        batchIds = failedBatches.Select(batch => batch.Id).ToArray();
        var staleJobs = await dbContext.NarrationJobs
            .Where(job => job.OwnerId == ownerId
                && job.SeriesId == seriesId
                && job.RebuildBatchId != null
                && batchIds.Contains(job.RebuildBatchId!.Value)
                && job.Mode == NarrationMode.MultiCharacter
                && job.Visibility == NarrationArtifactVisibility.Staged)
            .ToArrayAsync(cancellationToken);
        var staleJobIds = staleJobs.Select(job => job.Id).ToArray();
        var purgeableAudioRelativePaths = staleJobs
            .Where(job => !string.IsNullOrWhiteSpace(job.AudioRelativePath))
            .Select(job => job.AudioRelativePath!)
            .ToArray();
        var stalePlanLinks = await dbContext.NarrationJobSpeechPlans
            .Where(link => staleJobIds.Contains(link.NarrationJobId))
            .ToArrayAsync(cancellationToken);

        dbContext.NarrationJobSpeechPlans.RemoveRange(stalePlanLinks);
        dbContext.NarrationJobs.RemoveRange(staleJobs);
        dbContext.SeriesCastRebuildBatches.RemoveRange(failedBatches);

        var draftCastRevisionIds = failedBatches.Select(batch => batch.DraftCastRevisionId).Distinct().ToArray();
        var stillReferencedRevisionIds = await dbContext.SeriesCastRebuildBatches
            .Where(batch => !batchIds.Contains(batch.Id) && draftCastRevisionIds.Contains(batch.DraftCastRevisionId))
            .Select(batch => batch.DraftCastRevisionId)
            .ToArrayAsync(cancellationToken);
        var purgeableRevisionIds = draftCastRevisionIds.Except(stillReferencedRevisionIds).ToArray();
        var purgeableDraftRevisions = await dbContext.NarrationCastRevisions
            .Include(revision => revision.Assignments)
            .Where(revision => purgeableRevisionIds.Contains(revision.Id)
                && revision.Status == NarrationCastRevisionStatus.Draft)
            .ToArrayAsync(cancellationToken);
        dbContext.NarrationCastRevisions.RemoveRange(purgeableDraftRevisions);

        await dbContext.SaveChangesAsync(cancellationToken);

        // 資料列成功刪除後才清音檔（順序反過來會在刪列失敗時遺失檔案）。
        // 沒有這一步的話，失敗批次裡已完成書冊的 MP3 會永久留在磁碟上。
        DeletePurgedAudioFiles(purgeableAudioRelativePaths);
    }

    private void DeletePurgedAudioFiles(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return;
        }

        var root = Path.GetFullPath(narrationOptions.Value.AudioRootPath);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var relativePath in relativePaths)
        {
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                File.Delete(fullPath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                // 檔案清理失敗不可以阻擋 purge 本身；殘檔留待下次人工或後續清理。
            }
        }
    }

    private async Task<Dictionary<Guid, Book>> LoadSeriesBooksAsync(
        Guid ownerId,
        IReadOnlyCollection<SeriesBook> memberships,
        CancellationToken cancellationToken)
    {
        var bookIds = memberships.Select(member => member.BookId).ToArray();
        var books = await dbContext.Books
            .AsNoTracking()
            .AsSplitQuery()
            .Include(book => book.Chapters)
            .Where(book => book.OwnerId == ownerId && bookIds.Contains(book.Id))
            .ToListAsync(cancellationToken);
        if (books.Count != bookIds.Length)
        {
            throw new InvalidOperationException("系列冊次包含不屬於目前擁有者的正文書籍。");
        }

        if (books.Any(book => !AuthorizedTextPolicy.IsProcessable(book)))
        {
            throw new NarrationTextUnavailableException();
        }

        return books.ToDictionary(book => book.Id);
    }

    private async Task<IReadOnlyDictionary<(Guid BookId, Guid ChapterId), ConfirmedSpeechPlanRevision>> LoadLatestConfirmedPlansAsync(
        Guid ownerId,
        Guid seriesId,
        IReadOnlyCollection<Guid> bookIds,
        CancellationToken cancellationToken)
    {
        var revisions = await dbContext.ConfirmedSpeechPlanRevisions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(revision => revision.Segments)
            .Where(revision => revision.OwnerId == ownerId
                && revision.SeriesId == seriesId
                && bookIds.Contains(revision.BookId))
            .OrderByDescending(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken);
        var drafts = await dbContext.ChapterSpeechPlanDrafts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(draft => draft.Segments)
            .Where(draft => draft.OwnerId == ownerId
                && draft.SeriesId == seriesId
                && bookIds.Contains(draft.BookId))
            .ToDictionaryAsync(draft => (draft.BookId, draft.ChapterId), cancellationToken);

        var result = new Dictionary<(Guid BookId, Guid ChapterId), ConfirmedSpeechPlanRevision>();
        foreach (var group in revisions.GroupBy(revision => (revision.BookId, revision.ChapterId)))
        {
            if (!drafts.TryGetValue(group.Key, out var draft)
                || draft.Status != ChapterSpeechPlanDraftStatus.ReadyToConfirm)
            {
                continue;
            }

            var matchingRevision = group.FirstOrDefault(revision => revision.MatchesDraft(draft));
            if (matchingRevision is not null)
            {
                result.Add(group.Key, matchingRevision);
            }
        }

        return result;
    }

    private Dictionary<Guid, SourceAndPlanSnapshot> BuildSourceAndPlanSnapshots(
        IReadOnlyCollection<SeriesBook> memberships,
        IReadOnlyDictionary<Guid, Book> sourceBooks,
        IReadOnlyDictionary<(Guid BookId, Guid ChapterId), ConfirmedSpeechPlanRevision> confirmedPlans,
        NarrativeVoiceMode narrativeVoiceMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new Dictionary<Guid, SourceAndPlanSnapshot>();
        foreach (var membership in memberships)
        {
            var book = sourceBooks[membership.BookId];
            var chapterPlans = new List<ChapterPlanSnapshot>();
            foreach (var chapter in book.Chapters.OrderBy(chapter => chapter.SortOrder).ThenBy(chapter => chapter.Id))
            {
                if (!confirmedPlans.TryGetValue((book.Id, chapter.Id), out var revision))
                {
                    throw new InvalidOperationException("系列所有冊次的每個章節都必須先確認 speech plan，才能建立多聲線批次。");
                }

                var currentPlan = segmenter.Segment(chapter.Title, chapter.OriginalText);
                if (!string.Equals(currentPlan.SourceHash, revision.SourceHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("至少一份確認過的 speech plan 已經過期，請重新產生並確認後再建立多聲線批次。");
                }

                if (narrativeVoiceMode == NarrativeVoiceMode.PointOfViewInnerMonologue
                    && revision.Segments.Any(segment => segment.Kind == SpeechSegmentTurnKind.Narrator))
                {
                    throw new InvalidOperationException("主角視角敘述模式仍存在旁白片段，請重新產生並確認該章劇本。");
                }

                chapterPlans.Add(new ChapterPlanSnapshot(chapter.SortOrder, revision));
            }

            result.Add(
                book.Id,
                new SourceAndPlanSnapshot(
                    NarrationSource.Create(book.Chapters.Select(chapter => new NarrationChapterSource(
                        chapter.Id,
                        chapter.SortOrder,
                        chapter.Title,
                        chapter.OriginalText))),
                    chapterPlans));
        }

        return result;
    }

    private static void EnsureBlueMagpieJobAdmissionBudget(
        IReadOnlyDictionary<Guid, SourceAndPlanSnapshot> sourceAndPlans,
        BlueMagpieOptions options)
    {
        foreach (var snapshot in sourceAndPlans.Values)
        {
            long estimatedChunks = 0;
            foreach (var segment in snapshot.Plans
                         .SelectMany(plan => plan.Revision.Segments))
            {
                // Confirmed offsets use UTF-16 code units. Treating each code unit as a scalar is
                // conservative for surrogate pairs. The runtime splitter never emits a non-final
                // chunk shorter than 61 scalars, and estimating every confirmed segment as its own
                // turn also safely overstates what adjacent-profile turn merging can produce.
                estimatedChunks = checked(
                    estimatedChunks
                    + ((segment.Length + (long)BlueMagpieOptions.MinimumTextScalarsPerNonFinalChunk - 1)
                        / BlueMagpieOptions.MinimumTextScalarsPerNonFinalChunk));
                if (estimatedChunks > options.MaximumChunksPerJob)
                {
                    throw new InvalidOperationException(
                        "BlueMagpie 配音工作預估分段數超過管理員設定的安全上限。請拆分書冊後再建立配音批次。");
                }
            }

            // Every gateway WAV is pinned PCM16/48 kHz/mono and cannot exceed
            // MaximumResponseBytes. Reserving that full amount for every conservatively estimated
            // chunk is deliberately stricter than expected speech duration and prevents a job
            // from being staged when its raw audio could cross the runtime aggregate ceiling.
            if (estimatedChunks > options.MaximumJobAudioBytes / options.MaximumResponseBytes)
            {
                throw new InvalidOperationException(
                    "BlueMagpie 配音工作預估 PCM 音訊量超過管理員設定的安全上限。請拆分書冊後再建立配音批次。");
            }
        }
    }

    private static void EnsureSingleSynthesisProvider(StorySeries series)
    {
        // The 3wa VoxCPM2 job dispatch provider also knows how to fall back to plain Edge voices
        // turn-by-turn (narrator voice cloning isn't wired up yet — see
        // ThreeWaVoxCpm2NarrationProvider), so a series staged on it may freely mix Edge-voiced
        // characters alongside 3wa-cloned ones. Any other provider must still be uniform across
        // the whole cast, since nothing else can route a mixed turn stream.
        var allowsEdgeFallback = string.Equals(
            series.NarratorProvider,
            CharacterVoiceProviders.ThreeWaVoxCpm2,
            StringComparison.OrdinalIgnoreCase);

        if (series.Characters.Any(character =>
            !string.Equals(character.VoiceProvider, series.NarratorProvider, StringComparison.OrdinalIgnoreCase)
            && !(allowsEdgeFallback
                && string.Equals(character.VoiceProvider, CharacterVoiceProviders.Edge, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("目前多聲線合成必須使用與旁白相同的語音 provider（3wa 聲線系列除外，角色可混用 Edge 聲線）。");
        }
    }

    private async Task EnsureThreeWaCharacterProfilesAvailableAsync(
        Guid ownerId,
        StorySeries series,
        CancellationToken cancellationToken)
    {
        var customCharacters = series.Characters
            .Where(character => string.Equals(
                character.VoiceProvider,
                CharacterVoiceProviders.ThreeWaVoxCpm2,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (customCharacters.Length == 0)
        {
            return;
        }

        if (customCharacters.Any(character => character.CharacterProfileId is null))
        {
            throw new InvalidOperationException(
                ThreeWaSynthesisCapabilities.CloneVoiceUnavailableMessage);
        }

        var characterProfileIds = customCharacters
            .Select(character => character.CharacterProfileId!.Value)
            .Distinct()
            .ToArray();
        var containsDesignProfile = await dbContext.CharacterVoiceProfiles
            .AsNoTracking()
            .AnyAsync(
                profile => profile.OwnerId == ownerId
                    && characterProfileIds.Contains(profile.CharacterProfileId)
                    && profile.Mode == CharacterVoiceProfileMode.Design,
                cancellationToken);
        if (containsDesignProfile)
        {
            throw new InvalidOperationException(
                ThreeWaSynthesisCapabilities.DesignVoiceUnavailableMessage);
        }

        var readyCloneBaseProfileIds = await dbContext.CharacterVoiceProfiles
            .AsNoTracking()
            .Where(profile => profile.OwnerId == ownerId
                && characterProfileIds.Contains(profile.CharacterProfileId)
                && profile.Kind == CharacterVoiceProfileKind.Base
                && profile.Mode == CharacterVoiceProfileMode.Clone
                && profile.Status == CharacterVoiceProfileStatus.Ready
                && dbContext.CharacterVoiceProfileOperations.Any(operation =>
                    operation.OwnerId == ownerId
                    && operation.CharacterProfileId == profile.CharacterProfileId
                    && operation.NewProfileId == profile.Id
                    && operation.State == CharacterVoiceProfileOperationState.Activated
                    && operation.EvidenceVersion == CharacterVoiceConsentEvidence.CurrentEvidenceVersion
                    && operation.AttestationVersion == CharacterVoiceConsentEvidence.CurrentAttestationVersion
                    && operation.FormalNarrationAllowed))
            .Select(profile => profile.CharacterProfileId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (characterProfileIds.Except(readyCloneBaseProfileIds).Any())
        {
            throw new InvalidOperationException(
                ThreeWaSynthesisCapabilities.CloneVoiceUnavailableMessage);
        }
    }

    private static void ValidateComposition(MultiCharacterNarrationOptions options, StorySeries series)
    {
        if (options.ChapterPauseMs is < 0 or > 5_000)
        {
            throw new InvalidOperationException("多聲線合成設定不完整。");
        }

        _ = options.ResolveProviderVersion(series.NarratorProvider);
        _ = options.ResolveCompositionVersion(series.NarratorProvider);
        _ = options.ResolveFfmpegProfile(series.NarratorProvider);
        foreach (var provider in series.Characters
            .Select(character => character.VoiceProvider)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _ = options.ResolveProviderVersion(provider);
        }
    }

    private static string BuildEffectiveCompositionVersion(
        string compositionVersion,
        NarrativeVoiceMode narrativeVoiceMode,
        Guid? pointOfViewCharacterId)
    {
        if (narrativeVoiceMode == NarrativeVoiceMode.IndependentNarrator)
        {
            return compositionVersion;
        }

        var policy = $"{narrativeVoiceMode}:{pointOfViewCharacterId?.ToString("N", CultureInfo.InvariantCulture) ?? "none"}";
        var policyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policy)))
            .ToLowerInvariant()[..12];
        return $"{compositionVersion}-nv-{policyHash}";
    }

    private static SeriesNarrationRebuildResponse ToResponse(SeriesCastRebuildBatch batch) =>
        new(
            batch.Id,
            batch.SeriesId,
            batch.BaseActiveCastRevisionId,
            batch.DraftCastRevisionId,
            batch.CohortMembershipRevision,
            batch.Status.ToString(),
            batch.CreatedAt,
            batch.UpdatedAt,
            batch.Members
                .OrderBy(member => member.MembershipRevision)
                .ThenBy(member => member.SeriesBookId)
                .Select(member => new SeriesNarrationRebuildMemberResponse(
                    member.Id,
                    member.SeriesBookId,
                    member.BookId,
                    member.MembershipRevision,
                    member.PreviousActiveNarrationJobId,
                    member.StagedNarrationJobId,
                    member.Status.ToString()))
                .ToArray());

    private async Task<IDbContextTransaction?> BeginTransactionAsync(
        CancellationToken cancellationToken,
        IsolationLevel isolationLevel = IsolationLevel.Serializable) =>
        dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(isolationLevel, cancellationToken)
            : null;

    private Guid EnsureCurrentOwnerId()
    {
        if (currentUser.UserId == Guid.Empty)
        {
            throw new InvalidOperationException("目前使用者識別碼無效。");
        }

        return currentUser.UserId;
    }

    private static void EnsureId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("識別碼不可為空白。", parameterName);
        }
    }

    private sealed record SourceAndPlanSnapshot(
        NarrationSourceDocument Source,
        IReadOnlyList<ChapterPlanSnapshot> Plans);

    private sealed record ChapterPlanSnapshot(
        int ChapterSortOrder,
        ConfirmedSpeechPlanRevision Revision);
}

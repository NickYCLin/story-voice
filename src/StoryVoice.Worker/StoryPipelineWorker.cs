using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Books;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Worker;

public sealed class StoryPipelineWorker(
    IServiceScopeFactory scopeFactory,
    INarrationProvider provider,
    NarrationProviderDispatcher multiVoiceDispatcher,
    IOptions<NarrationOptions> options,
    IOptions<BlueMagpieOptions> blueMagpieOptions,
    ILogger<StoryPipelineWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTimeOffset _nextRecoveryAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("StoryVoice narration worker {WorkerId} is ready with {MaximumConcurrentJobs} processing slots",
            _workerId, options.Value.MaximumConcurrentJobs);
        Directory.CreateDirectory(options.Value.AudioRootPath);
        await NarrationJobScheduler.RunAsync(
            options.Value.MaximumConcurrentJobs, ClaimNextAsync, ProcessAsync,
            exception => logger.LogError(exception, "Narration worker loop failed"), stoppingToken);
    }

    private async Task<ClaimedJob?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var now = DateTimeOffset.UtcNow;
        if (now >= _nextRecoveryAt)
        {
            await SupportedJobs(db.NarrationJobs)
                .Where(job => job.Status == NarrationJobStatus.Running
                    && job.LeaseExpiresAt != null
                    && job.LeaseExpiresAt <= now
                    && job.CancellationRequested)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, NarrationJobStatus.Cancelled)
                    .SetProperty(job => job.ProgressPercent, 0)
                    .SetProperty(job => job.LeaseOwner, (string?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.ErrorCode, (string?)null)
                    .SetProperty(job => job.UpdatedAt, now)
                    .SetProperty(job => job.ConcurrencyStamp, Guid.NewGuid()),
                    cancellationToken);
            var expiredVoAiJobIds = await SupportedJobs(db.NarrationJobs)
                .Where(job => job.Status == NarrationJobStatus.Running
                    && job.LeaseExpiresAt != null
                    && job.LeaseExpiresAt <= now
                    && !job.CancellationRequested
                    && job.Mode == NarrationMode.MultiCharacter
                    && job.CastRevisionId != null
                    && db.NarrationCastRevisions.Any(revision =>
                        revision.Id == job.CastRevisionId
                        && revision.NarratorProvider == CharacterVoiceProviders.VoAi))
                .Select(job => job.Id)
                .ToArrayAsync(cancellationToken);
            if (expiredVoAiJobIds.Length > 0)
            {
                await SupportedJobs(db.NarrationJobs)
                    .Where(job => expiredVoAiJobIds.Contains(job.Id)
                        && job.Status == NarrationJobStatus.Running
                        && job.LeaseExpiresAt != null
                        && job.LeaseExpiresAt <= now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.Status, NarrationJobStatus.Failed)
                        .SetProperty(job => job.ProgressPercent, 0)
                        .SetProperty(job => job.LeaseOwner, (string?)null)
                        .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(job => job.NextAttemptAt, (DateTimeOffset?)null)
                        .SetProperty(job => job.ErrorCode, "voai_lease_expired")
                        .SetProperty(job => job.UpdatedAt, now)
                        .SetProperty(job => job.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);
            }
            await SupportedJobs(db.NarrationJobs)
                .Where(job => job.Status == NarrationJobStatus.Running
                    && job.LeaseExpiresAt != null
                    && job.LeaseExpiresAt <= now
                    && !job.CancellationRequested
                    && job.Attempts >= options.Value.MaxAttempts)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, NarrationJobStatus.Failed)
                    .SetProperty(job => job.ProgressPercent, 0)
                    .SetProperty(job => job.LeaseOwner, (string?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.ErrorCode, "worker_lease_expired")
                    .SetProperty(job => job.UpdatedAt, now)
                    .SetProperty(job => job.ConcurrencyStamp, Guid.NewGuid()),
                    cancellationToken);
            await SynchronizeTerminalStagedBatchesAsync(db, cancellationToken);
            _nextRecoveryAt = now.AddSeconds(30);
        }

        var candidateId = await SupportedJobs(db.NarrationJobs)
            .AsNoTracking()
            .Where(job => !job.CancellationRequested
                && job.Attempts < options.Value.MaxAttempts
                && ((job.Status == NarrationJobStatus.Queued
                        && (job.NextAttemptAt == null || job.NextAttemptAt <= now))
                    || (job.Status == NarrationJobStatus.Running
                        && job.LeaseExpiresAt != null
                        && job.LeaseExpiresAt <= now)))
            .OrderBy(job => job.NextAttemptAt)
            .ThenBy(job => job.CreatedAt)
            .Select(job => (Guid?)job.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidateId is null)
        {
            return null;
        }

        var artifactToken = Guid.NewGuid().ToString("N");
        var leaseOwner = $"{_workerId}:{artifactToken}";
        var leaseExpiresAt = now.AddMinutes(options.Value.LeaseMinutes);
        var affected = await SupportedJobs(db.NarrationJobs)
            .Where(job => job.Id == candidateId
                && !job.CancellationRequested
                && job.Attempts < options.Value.MaxAttempts
                && ((job.Status == NarrationJobStatus.Queued
                        && (job.NextAttemptAt == null || job.NextAttemptAt <= now))
                    || (job.Status == NarrationJobStatus.Running
                        && job.LeaseExpiresAt != null
                        && job.LeaseExpiresAt <= now)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, NarrationJobStatus.Running)
                .SetProperty(job => job.ProgressPercent, 10)
                .SetProperty(job => job.Attempts, job => job.Attempts + 1)
                .SetProperty(job => job.LeaseOwner, leaseOwner)
                .SetProperty(job => job.LeaseExpiresAt, leaseExpiresAt)
                .SetProperty(job => job.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(job => job.ErrorCode, (string?)null)
                .SetProperty(job => job.UpdatedAt, now)
                .SetProperty(job => job.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return affected == 1
            ? new ClaimedJob(candidateId.Value, leaseOwner, artifactToken)
            : null;
    }

    private async Task ProcessAsync(ClaimedJob claim, CancellationToken stoppingToken)
    {
        string? temporaryPath = null;
        string? uncommittedAudioPath = null;
        string? synthesisProviderName = null;
        NarrationUsageRecorder? usage = null;
        var completionUncertain = false;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var job = await SupportedJobs(db.NarrationJobs)
                .AsNoTracking()
                .Where(item => item.Id == claim.JobId
                    && item.Status == NarrationJobStatus.Running
                    && item.LeaseOwner == claim.LeaseOwner)
                .Select(item => new
                {
                    item.Id,
                    item.OwnerId,
                    item.ContentBookId,
                    item.SourceHash,
                    item.Voice,
                    item.Rate,
                    item.Mode,
                    item.SeriesId,
                    item.CastRevisionId,
                    item.CancellationRequested
                })
                .SingleOrDefaultAsync(stoppingToken);
            if (job is null)
            {
                return;
            }

            usage = new NarrationUsageRecorder(scopeFactory, logger, Guid.ParseExact(claim.ArtifactToken, "N"));
            await usage.StartAsync(job.OwnerId, job.Id, claim.LeaseOwner);

            if (job.CancellationRequested)
            {
                usage.Outcome = "Cancelled";
                await RecordFailureAsync(claim, "cancelled", CancellationToken.None);
                return;
            }

            var content = await db.Books
                .AsNoTracking()
                .Include(book => book.Chapters)
                .SingleOrDefaultAsync(
                    book => book.Id == job.ContentBookId && book.OwnerId == job.OwnerId,
                    stoppingToken);
            if (!AuthorizedTextPolicy.IsProcessable(content))
            {
                throw new InvalidOperationException("narration_source_unavailable");
            }

            var source = NarrationSource.Create(content!.Chapters.Select(chapter =>
                new NarrationChapterSource(chapter.Id, chapter.SortOrder, chapter.Title, chapter.OriginalText)));
            if (!string.Equals(source.SourceHash, job.SourceHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("narration_source_changed");
            }

            var relativePath = Path.Combine(
                job.OwnerId.ToString("N"),
                job.Id.ToString("N"),
                $"{claim.ArtifactToken}.mp3");
            var absolutePath = Path.Combine(options.Value.AudioRootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            temporaryPath = absolutePath + $".tmp-{Guid.NewGuid():N}";

            NarrationTimelineDocument? timelineDocument = null;
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(options.Value.ProviderTimeoutMinutes));
            using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                timeout.Token);
            var cancellationMonitor = MonitorCancellationAsync(claim, providerCancellation, stoppingToken);
            try
            {
                var progressCheckpoint = new SynthesisProgressCheckpoint();
                Func<NarrationSynthesisProgress, CancellationToken, Task> reportProgress = async (progress, progressCancellationToken) =>
                {
                    usage.ReportProgress(progress);
                    if (progressCheckpoint.TryAdvance(progress, out var progressPercent))
                    {
                        await ReportSynthesisProgressAsync(
                            db,
                            claim,
                            progressPercent,
                            progressCancellationToken);
                    }
                };

                if (job.Mode == NarrationMode.MultiCharacter)
                {
                    var castRevision = await LoadCastRevisionAsync(
                        db,
                        job.OwnerId,
                        job.SeriesId!.Value,
                        job.CastRevisionId!.Value,
                        stoppingToken);
                    synthesisProviderName = castRevision.NarratorProvider;
                    if (string.Equals(
                            castRevision.NarratorProvider,
                            CharacterVoiceProviders.ThreeWaVoxCpm2,
                            StringComparison.OrdinalIgnoreCase)
                        && !ThreeWaSynthesisCapabilities.SupportsTrustedCloneFormalNarration)
                    {
                        throw new PermanentNarrationProviderException(
                            ThreeWaSynthesisCapabilities.CloneFormalNarrationUnavailableCode,
                            ThreeWaSynthesisCapabilities.CloneFormalNarrationUnavailableMessage);
                    }

                    var chapterPlans = await LoadChapterPlanSourcesAsync(
                        db,
                        job.OwnerId,
                        job.SeriesId.Value,
                        job.Id,
                        content,
                        stoppingToken);
                    var (voiceProfiles, characterProfileIdsByCharacterId) = await LoadCharacterVoiceProfilesAsync(
                        db,
                        job.OwnerId,
                        job.SeriesId.Value,
                        stoppingToken);
                    var turnPlan = MultiCharacterTurnBuilder.BuildTurnPlan(
                        castRevision,
                        chapterPlans,
                        voiceProfiles,
                        characterProfileIdsByCharacterId);
                    var cacheContext = new NarrationSynthesisCacheContext(
                        job.OwnerId,
                        job.Id,
                        job.SourceHash,
                        castRevision.Id,
                        castRevision.Fingerprint,
                        ComputeSpeechPlanFingerprint(chapterPlans),
                        castRevision.CompositionVersion,
                        castRevision.FfmpegProfile);
                    await usage.BeginSynthesisAsync(castRevision.NarratorProvider, turnPlan.Turns.Select(turn => turn.Text));
                    var synthesisResult = await multiVoiceDispatcher.SynthesizeAsync(
                        castRevision.NarratorProvider,
                        CreateMultiVoiceNarrationRequest(castRevision, turnPlan.Turns, cacheContext),
                        temporaryPath,
                        reportProgress,
                        providerCancellation.Token);
                    timelineDocument = NarrationTimelineComposer.Compose(
                        turnPlan.Sources,
                        synthesisResult.TurnTimings);
                }
                else
                {
                    await usage.BeginSynthesisAsync("edge", [source.Text]);
                    await provider.SynthesizeAsync(
                        source.Text,
                        temporaryPath,
                        job.Voice,
                        job.Rate,
                        reportProgress,
                        providerCancellation.Token);
                }
            }
            finally
            {
                usage.EndSynthesis();
                providerCancellation.Cancel();
                await IgnoreCancellationAsync(cancellationMonitor);
            }

            if (!File.Exists(temporaryPath))
            {
                throw new InvalidOperationException("provider_empty_audio");
            }

            var audioBytes = new FileInfo(temporaryPath).Length;
            if (audioBytes < 1)
            {
                throw new InvalidOperationException("provider_empty_audio");
            }

            File.Move(temporaryPath, absolutePath, overwrite: false);
            temporaryPath = null;
            uncommittedAudioPath = absolutePath;
            await PersistTimelineAsync(db, job.OwnerId, claim.JobId, timelineDocument, stoppingToken);
            var completedAt = DateTimeOffset.UtcNow;
            var persistedRelativePath = relativePath.Replace('\\', '/');
            int affected;
            try
            {
                affected = await SupportedJobs(db.NarrationJobs)
                    .Where(item => item.Id == claim.JobId
                        && item.Status == NarrationJobStatus.Running
                        && item.LeaseOwner == claim.LeaseOwner
                        && !item.CancellationRequested)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, NarrationJobStatus.Completed)
                        .SetProperty(item => item.ProgressPercent, 100)
                        .SetProperty(item => item.AudioRelativePath, persistedRelativePath)
                        .SetProperty(item => item.AudioBytes, audioBytes)
                        .SetProperty(item => item.LeaseOwner, (string?)null)
                        .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.NextAttemptAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.CompletedAt, completedAt)
                        .SetProperty(item => item.UpdatedAt, completedAt)
                        .SetProperty(item => item.ConcurrencyStamp, Guid.NewGuid()),
                        stoppingToken);
            }
            catch (Exception completionException)
            {
                var completionConfirmed = await VerifyCompletedArtifactAsync(
                    claim.JobId,
                    persistedRelativePath,
                    audioBytes);
                var outcome = NarrationCompletionOutcomeResolver.ResolveAfterAmbiguousCommand(completionConfirmed);
                if (outcome.AcceptCompletion)
                {
                    usage.Outcome = "Completed";
                    usage.AudioBytes = audioBytes;
                    uncommittedAudioPath = null;
                    await SynchronizeStagedBatchAsync(claim.JobId, CancellationToken.None);
                    logger.LogWarning(
                        completionException,
                        "Narration job {JobId} completion was committed despite an ambiguous database response",
                        claim.JobId);
                    return;
                }

                if (!outcome.DeleteCandidateArtifact)
                {
                    completionUncertain = true;
                    // Preserve the uniquely named artifact when durable state cannot be re-read.
                    // Deleting it here could leave a committed Completed row pointing at a missing file.
                    uncommittedAudioPath = null;
                }

                throw;
            }

            if (affected != 1)
            {
                usage.Outcome = "LeaseLost";
                await RecordFailureAsync(claim, "cancelled", CancellationToken.None);
                return;
            }

            uncommittedAudioPath = null;
            usage.Outcome = "Completed";
            usage.AudioBytes = audioBytes;
            await SynchronizeStagedBatchAsync(claim.JobId, CancellationToken.None);
            logger.LogInformation(
                "Narration job {JobId} completed with {AudioBytes} bytes",
                claim.JobId,
                audioBytes);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            if (!completionUncertain && usage is { Outcome: not "Completed" }) usage.Outcome = "WorkerStopped";
            if (CanResumeAfterWorkerStop(synthesisProviderName))
            {
                await RecordFailureAsync(
                    claim,
                    "worker_stopped",
                    CancellationToken.None,
                    retryDelay: blueMagpieOptions.Value.RestartCooldown);
                return;
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            if (!completionUncertain && usage is { Outcome: not "Completed" }) usage.Outcome = "TimedOut";
            var preventAutomaticReplay = string.Equals(
                synthesisProviderName,
                CharacterVoiceProviders.VoAi,
                StringComparison.OrdinalIgnoreCase);
            await RecordFailureAsync(
                claim,
                "provider_timeout",
                CancellationToken.None,
                permanent: preventAutomaticReplay,
                retryDelay: ResolveProviderRetryDelay(
                    synthesisProviderName,
                    preventAutomaticReplay,
                    blueMagpieOptions.Value));
        }
        catch (Exception exception)
        {
            if (!completionUncertain && usage is { Outcome: not "Completed" }) usage.Outcome = "Failed";
            logger.LogWarning(exception, "Narration job {JobId} failed", claim.JobId);
            var permanentProviderFailure = exception as PermanentNarrationProviderException;
            var isVoAiJob = string.Equals(
                synthesisProviderName,
                CharacterVoiceProviders.VoAi,
                StringComparison.OrdinalIgnoreCase);
            var isPermanentErrorCode = permanentProviderFailure is not null
                || isVoAiJob
                || exception.Message is "narration_source_unavailable"
                or "narration_source_changed"
                or MultiCharacterTurnBuilder.IntegrityMismatchReasonCode;
            var errorCode = permanentProviderFailure?.ErrorCode
                ?? (isVoAiJob
                    ? "voai_provider_failed"
                    : isPermanentErrorCode ? exception.Message : "provider_failed");
            await RecordFailureAsync(
                claim,
                errorCode,
                CancellationToken.None,
                permanent: isPermanentErrorCode,
                retryDelay: ResolveProviderRetryDelay(
                    synthesisProviderName,
                    isPermanentErrorCode,
                    blueMagpieOptions.Value));
        }
        finally
        {
            try
            {
                DeleteIfExists(temporaryPath);
                DeleteIfExists(uncommittedAudioPath);
            }
            finally
            {
                if (usage is not null) await usage.FinishAsync();
            }
        }
    }

    private static async Task<NarrationCastRevision> LoadCastRevisionAsync(
        StoryVoiceDbContext db,
        Guid ownerId,
        Guid seriesId,
        Guid castRevisionId,
        CancellationToken cancellationToken)
    {
        var castRevision = await db.NarrationCastRevisions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(revision => revision.Assignments)
            .SingleOrDefaultAsync(
                revision => revision.OwnerId == ownerId
                    && revision.SeriesId == seriesId
                    && revision.Id == castRevisionId,
                cancellationToken);
        if (castRevision is null || !castRevision.VerifyFingerprint())
        {
            throw new InvalidOperationException(MultiCharacterTurnBuilder.IntegrityMismatchReasonCode);
        }

        return castRevision;
    }

    private static async Task<(IReadOnlyList<CharacterVoiceProfile> Profiles, IReadOnlyDictionary<Guid, Guid> CharacterProfileIdsByCharacterId)>
        LoadCharacterVoiceProfilesAsync(
            StoryVoiceDbContext db,
            Guid ownerId,
            Guid seriesId,
            CancellationToken cancellationToken)
    {
        var characterProfileIdsByCharacterId = await db.SeriesCharacters
            .AsNoTracking()
            .Where(character => character.OwnerId == ownerId
                && character.SeriesId == seriesId
                && character.CharacterProfileId != null)
            .ToDictionaryAsync(
                character => character.Id,
                character => character.CharacterProfileId!.Value,
                cancellationToken);
        if (characterProfileIdsByCharacterId.Count == 0)
        {
            return ([], characterProfileIdsByCharacterId);
        }

        var characterProfileIds = characterProfileIdsByCharacterId.Values.Distinct().ToArray();
        var profiles = await db.CharacterVoiceProfiles
            .AsNoTracking()
            .Where(profile => profile.OwnerId == ownerId
                && characterProfileIds.Contains(profile.CharacterProfileId)
                && (profile.Mode != CharacterVoiceProfileMode.Clone
                    || db.CharacterVoiceProfileOperations.Any(operation =>
                        operation.OwnerId == ownerId
                        && operation.CharacterProfileId == profile.CharacterProfileId
                        && operation.NewProfileId == profile.Id
                        && operation.State == CharacterVoiceProfileOperationState.Activated
                        && operation.EvidenceVersion == CharacterVoiceConsentEvidence.CurrentEvidenceVersion
                        && operation.AttestationVersion == CharacterVoiceConsentEvidence.CurrentAttestationVersion
                        && operation.FormalNarrationAllowed)))
            .ToListAsync(cancellationToken);
        return (profiles, characterProfileIdsByCharacterId);
    }

    /// <summary>
    /// Best-effort by design: the MP3 already exists, so a failed timeline write only costs
    /// playback sync — it must never fail (or roll back) the completed synthesis itself.
    /// </summary>
    private async Task PersistTimelineAsync(
        StoryVoiceDbContext db,
        Guid ownerId,
        Guid jobId,
        NarrationTimelineDocument? timelineDocument,
        CancellationToken cancellationToken)
    {
        if (timelineDocument is null)
        {
            return;
        }

        try
        {
            await db.NarrationTimelines
                .Where(timeline => timeline.NarrationJobId == jobId)
                .ExecuteDeleteAsync(cancellationToken);
            db.NarrationTimelines.Add(NarrationTimeline.Create(ownerId, jobId, timelineDocument.ToJson()));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Unable to persist the playback timeline for narration job {JobId}; playback sync stays unavailable",
                jobId);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<IReadOnlyList<ChapterPlanSource>> LoadChapterPlanSourcesAsync(
        StoryVoiceDbContext db,
        Guid ownerId,
        Guid seriesId,
        Guid narrationJobId,
        Book contentBook,
        CancellationToken cancellationToken)
    {
        var links = await db.NarrationJobSpeechPlans
            .AsNoTracking()
            .Where(link => link.OwnerId == ownerId
                && link.SeriesId == seriesId
                && link.NarrationJobId == narrationJobId)
            .OrderBy(link => link.ChapterSortOrder)
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            throw new InvalidOperationException(MultiCharacterTurnBuilder.IntegrityMismatchReasonCode);
        }

        var revisionIds = links.Select(link => link.ConfirmedSpeechPlanRevisionId).ToArray();
        var revisions = await db.ConfirmedSpeechPlanRevisions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(revision => revision.Segments)
            .Where(revision => revision.OwnerId == ownerId
                && revision.SeriesId == seriesId
                && revisionIds.Contains(revision.Id))
            .ToDictionaryAsync(revision => revision.Id, cancellationToken);
        var chaptersById = contentBook.Chapters.ToDictionary(chapter => chapter.Id);

        var chapterPlans = new List<ChapterPlanSource>(links.Count);
        foreach (var link in links)
        {
            if (!revisions.TryGetValue(link.ConfirmedSpeechPlanRevisionId, out var revision)
                || revision.BookId != contentBook.Id
                || !chaptersById.TryGetValue(revision.ChapterId, out var chapter))
            {
                throw new InvalidOperationException(MultiCharacterTurnBuilder.IntegrityMismatchReasonCode);
            }

            chapterPlans.Add(new ChapterPlanSource(link.ChapterSortOrder, revision, chapter.Title, chapter.OriginalText));
        }

        return chapterPlans;
    }

    internal static int CalculateSynthesisProgress(int completedChunks, int totalChunks)
    {
        if (completedChunks < 1 || totalChunks < 1 || completedChunks > totalChunks)
        {
            throw new ArgumentOutOfRangeException(nameof(completedChunks));
        }

        var mapped = 10 + (int)((long)completedChunks * 85 / totalChunks);
        return Math.Clamp(mapped, 11, 95);
    }

    internal sealed class SynthesisProgressCheckpoint
    {
        private int _lastProgressPercent = 10;

        public bool TryAdvance(
            NarrationSynthesisProgress progress,
            out int progressPercent)
        {
            ArgumentNullException.ThrowIfNull(progress);
            progressPercent = CalculateSynthesisProgress(
                progress.CompletedChunks,
                progress.TotalChunks);
            while (true)
            {
                var current = Volatile.Read(ref _lastProgressPercent);
                if (progressPercent <= current)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(
                    ref _lastProgressPercent,
                    progressPercent,
                    current) == current)
                {
                    return true;
                }
            }
        }
    }

    internal static MultiVoiceNarrationRequest CreateMultiVoiceNarrationRequest(
        NarrationCastRevision castRevision,
        IReadOnlyList<NarrationTurn> turns,
        NarrationSynthesisCacheContext? cacheContext = null)
    {
        ArgumentNullException.ThrowIfNull(castRevision);
        ArgumentNullException.ThrowIfNull(turns);
        return new MultiVoiceNarrationRequest(
            turns,
            new NarrationProviderContract(
                castRevision.NarratorProvider,
                castRevision.NarratorProviderVersion),
            castRevision.Assignments
                .Select(assignment => new NarrationProviderContract(
                    assignment.VoiceProvider,
                    assignment.ProviderVersion))
                .ToArray(),
            cacheContext);
    }

    internal static bool CanResumeAfterWorkerStop(string? providerName) =>
        string.Equals(
            providerName,
            CharacterVoiceProviders.BlueMagpie,
            StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan? ResolveProviderRetryDelay(
        string? providerName,
        bool permanent,
        BlueMagpieOptions blueOptions)
    {
        ArgumentNullException.ThrowIfNull(blueOptions);
        return !permanent && CanResumeAfterWorkerStop(providerName)
            ? blueOptions.RestartCooldown
            : null;
    }

    internal static DateTimeOffset? CalculateNextAttemptAt(
        DateTimeOffset now,
        int attempts,
        bool finalFailure,
        TimeSpan? retryDelay = null)
    {
        if (attempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts));
        }

        return finalFailure
            ? null
            : now.Add(retryDelay
                ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempts))));
    }

    internal static string ComputeSpeechPlanFingerprint(
        IReadOnlyList<ChapterPlanSource> chapterPlans)
    {
        ArgumentNullException.ThrowIfNull(chapterPlans);
        if (chapterPlans.Count == 0)
        {
            throw new ArgumentException(
                "At least one confirmed speech plan is required.",
                nameof(chapterPlans));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintField(hash, "StoryVoice.NarrationSpeechPlanSet.v1");
        foreach (var chapterPlan in chapterPlans.OrderBy(plan => plan.ChapterSortOrder))
        {
            AppendFingerprintField(
                hash,
                chapterPlan.ChapterSortOrder.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFingerprintField(hash, chapterPlan.Revision.Id.ToString("N"));
            AppendFingerprintField(hash, chapterPlan.Revision.Fingerprint);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFingerprintField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    internal static IQueryable<NarrationJob> SupportedJobs(IQueryable<NarrationJob> jobs) =>
        jobs.Where(job => job.Mode == NarrationMode.SingleVoice || job.Mode == NarrationMode.MultiCharacter);

    private async Task ReportSynthesisProgressAsync(
        StoryVoiceDbContext db,
        ClaimedJob claim,
        int progressPercent,
        CancellationToken cancellationToken)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        try
        {
            await SupportedJobs(db.NarrationJobs)
                .Where(item => item.Id == claim.JobId
                    && item.Status == NarrationJobStatus.Running
                    && item.LeaseOwner == claim.LeaseOwner
                    && item.ProgressPercent < progressPercent)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ProgressPercent, progressPercent)
                    .SetProperty(item => item.UpdatedAt, updatedAt)
                    .SetProperty(item => item.ConcurrencyStamp, Guid.NewGuid()),
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to persist narration progress for job {JobId}",
                claim.JobId);
        }
    }

    private async Task MonitorCancellationAsync(
        ClaimedJob claim,
        CancellationTokenSource providerCancellation,
        CancellationToken stoppingToken)
    {
        try
        {
            var renewEvery = TimeSpan.FromMinutes(Math.Max(1, options.Value.LeaseMinutes / 3d));
            var nextRenewalAt = DateTimeOffset.UtcNow.Add(renewEvery);
            while (!providerCancellation.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
                var state = await SupportedJobs(db.NarrationJobs)
                    .AsNoTracking()
                    .Where(job => job.Id == claim.JobId)
                    .Select(job => new { job.Status, job.CancellationRequested, job.LeaseOwner })
                    .SingleOrDefaultAsync(stoppingToken);
                if (state is null
                    || state.Status != NarrationJobStatus.Running
                    || state.CancellationRequested
                    || !string.Equals(state.LeaseOwner, claim.LeaseOwner, StringComparison.Ordinal))
                {
                    providerCancellation.Cancel();
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                if (now >= nextRenewalAt)
                {
                    var renewed = await SupportedJobs(db.NarrationJobs)
                        .Where(job => job.Id == claim.JobId
                            && job.Status == NarrationJobStatus.Running
                            && job.LeaseOwner == claim.LeaseOwner
                            && !job.CancellationRequested)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(job => job.LeaseExpiresAt, now.AddMinutes(options.Value.LeaseMinutes))
                            .SetProperty(job => job.UpdatedAt, now)
                            .SetProperty(job => job.ConcurrencyStamp, Guid.NewGuid()),
                            stoppingToken);
                    if (renewed != 1)
                    {
                        providerCancellation.Cancel();
                        return;
                    }

                    nextRenewalAt = now.Add(renewEvery);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // A short renewable lease permits prompt crash recovery. If its monitor cannot
            // confirm ownership, fail closed and cancel synthesis before another Worker can
            // reclaim the job after lease expiry.
            logger.LogWarning(
                exception,
                "Narration lease monitor failed for job {JobId}; cancelling provider execution",
                claim.JobId);
            providerCancellation.Cancel();
        }
    }

    private async Task<bool?> VerifyCompletedArtifactAsync(
        Guid jobId,
        string relativePath,
        long audioBytes)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            return await SupportedJobs(db.NarrationJobs)
                .AsNoTracking()
                .AnyAsync(job => job.Id == jobId
                    && job.Status == NarrationJobStatus.Completed
                    && job.AudioRelativePath == relativePath
                    && job.AudioBytes == audioBytes,
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not resolve ambiguous completion state for narration job {JobId}; preserving artifact",
                jobId);
            return null;
        }
    }

    private async Task RecordFailureAsync(
        ClaimedJob claim,
        string errorCode,
        CancellationToken cancellationToken,
        bool permanent = false,
        TimeSpan? retryDelay = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var state = await SupportedJobs(db.NarrationJobs)
            .AsNoTracking()
            .Where(job => job.Id == claim.JobId
                && job.Status == NarrationJobStatus.Running
                && job.LeaseOwner == claim.LeaseOwner)
            .Select(job => new { job.Attempts, job.CancellationRequested })
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (state.CancellationRequested)
        {
            await FinalizeCancellationAsync(db, claim, now, cancellationToken);
            await SynchronizeStagedBatchAsync(claim.JobId, CancellationToken.None);
            return;
        }

        var finalFailure = permanent || state.Attempts >= options.Value.MaxAttempts;
        var nextAttemptAt = CalculateNextAttemptAt(
            now,
            state.Attempts,
            finalFailure,
            retryDelay);
        var failureUpdated = await SupportedJobs(db.NarrationJobs)
            .Where(job => job.Id == claim.JobId
                && job.Status == NarrationJobStatus.Running
                && job.LeaseOwner == claim.LeaseOwner
                && !job.CancellationRequested)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    job => job.Status,
                    finalFailure ? NarrationJobStatus.Failed : NarrationJobStatus.Queued)
                .SetProperty(job => job.ProgressPercent, 0)
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(job => job.NextAttemptAt, nextAttemptAt)
                .SetProperty(job => job.ErrorCode, errorCode)
                .SetProperty(job => job.UpdatedAt, now)
                .SetProperty(job => job.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        if (failureUpdated != 1)
        {
            // Cancellation can commit after the state read above. Finalize it immediately
            // rather than waiting for the lease-recovery sweep.
            await FinalizeCancellationAsync(db, claim, DateTimeOffset.UtcNow, cancellationToken);
        }

        await SynchronizeStagedBatchAsync(claim.JobId, CancellationToken.None);
    }

    private async Task SynchronizeTerminalStagedBatchesAsync(
        StoryVoiceDbContext db,
        CancellationToken cancellationToken)
    {
        var jobIds = await SupportedJobs(db.NarrationJobs)
            .AsNoTracking()
            .Where(job => job.Mode == NarrationMode.MultiCharacter
                && job.Visibility == NarrationArtifactVisibility.Staged
                && job.RebuildBatchId != null
                && (job.Status == NarrationJobStatus.Completed
                    || job.Status == NarrationJobStatus.Failed
                    || job.Status == NarrationJobStatus.Cancelled))
            .Select(job => job.Id)
            .ToArrayAsync(cancellationToken);
        foreach (var jobId in jobIds)
        {
            await SynchronizeStagedBatchAsync(jobId, cancellationToken);
        }
    }

    private async Task SynchronizeStagedBatchAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var progress = scope.ServiceProvider.GetRequiredService<IStagedNarrationBatchProgressService>();
            await progress.SynchronizeAsync(jobId, cancellationToken);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested is false)
        {
            logger.LogError(
                exception,
                "Unable to synchronize staged narration job {JobId} with its rebuild batch",
                jobId);
        }
    }

    private static Task<int> FinalizeCancellationAsync(
        StoryVoiceDbContext db,
        ClaimedJob claim,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        SupportedJobs(db.NarrationJobs)
            .Where(job => job.Id == claim.JobId
                && job.Status == NarrationJobStatus.Running
                && job.LeaseOwner == claim.LeaseOwner
                && job.CancellationRequested)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, NarrationJobStatus.Cancelled)
                .SetProperty(job => job.ProgressPercent, 0)
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(job => job.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(job => job.ErrorCode, (string?)null)
                .SetProperty(job => job.UpdatedAt, now)
                .SetProperty(job => job.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void DeleteIfExists(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record ClaimedJob(Guid JobId, string LeaseOwner, string ArtifactToken);
}

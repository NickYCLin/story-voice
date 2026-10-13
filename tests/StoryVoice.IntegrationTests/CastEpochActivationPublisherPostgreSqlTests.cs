using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Domain.Series;
using StoryVoice.Infrastructure.Identity;
using StoryVoice.Infrastructure.Persistence;
using StoryVoice.Infrastructure.Persistence.Migrations;
using Testcontainers.PostgreSql;

namespace StoryVoice.IntegrationTests;

public sealed class CastEpochActivationPublisherPostgreSqlTests
{
    private const string PreviousMigration = "20260811040925_AddCastRebuildPersistence";
    private const string CurrentMigration = "20260811052803_AddAtomicCastEpochActivation";

    [Fact]
    public async Task Retained_failed_cohort_does_not_break_next_epoch_and_blocks_lossy_schema_downgrade()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(ct);
        var graph = await CreateReadyGraphAsync(postgres.GetConnectionString(), "retained-failure",
            useLegacyPredecessors: false, migration: "20261013042545_PreserveNarrationTracks", ct);
        await using (var db = CreateContext(graph.ConnectionString))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO series_cast_rebuild_batches
                    ("Id", "OwnerId", "SeriesId", "BaseActiveCastRevisionId", "DraftCastRevisionId", "CohortMembershipRevision", "CreatedAt", "UpdatedAt", "Status")
                SELECT {Guid.NewGuid()}, "OwnerId", "SeriesId", "BaseActiveCastRevisionId", "DraftCastRevisionId", "CohortMembershipRevision", "CreatedAt", "UpdatedAt", 'Failed'
                FROM series_cast_rebuild_batches WHERE "Id" = {graph.BatchId};
                """, ct);
            var blocked = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>()
                .MigrateAsync("20260908025724_AddNarrationAttemptUsage", ct));
            Assert.Contains("retained rebuild history", blocked.MessageText);
            Assert.Equal(2, await db.SeriesCastRebuildBatches.CountAsync(ct));
            Assert.Contains("20261013042545_PreserveNarrationTracks", await db.Database.GetAppliedMigrationsAsync(ct));
        }
        await ActivateAsync(graph, ct);
        var second = await AddReadyBatchAsync(graph, graph.RevisionId, graph.JobIds, 2, "retained-second", ct);
        var result = await ActivateAsync(graph.ConnectionString, graph.OwnerId, graph.SeriesId,
            second.BatchId, graph.ActivatedAt.AddMinutes(1), ct);
        Assert.Equal(2, result.EpochNumber);
        await using var proof = CreateContext(graph.ConnectionString);
        Assert.Equal(1, await proof.SeriesCastRebuildBatches.CountAsync(batch => batch.Status == SeriesCastRebuildBatchStatus.Failed, ct));
    }

    [Fact]
    public async Task Latest_migration_upgrades_ready_graph_and_first_activation_reloads_complete_epoch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var graph = await CreateReadyGraphAsync(
            postgres.GetConnectionString(),
            "epoch-upgrade-first",
            useLegacyPredecessors: false,
            migration: PreviousMigration,
            cancellationToken);

        await using (var upgrade = CreateContext(graph.ConnectionString))
        {
            await upgrade.GetService<IMigrator>().MigrateAsync(CurrentMigration, cancellationToken);
        }

        await using (var reloaded = CreateContext(graph.ConnectionString))
        {
            Assert.Contains(CurrentMigration, await reloaded.Database.GetAppliedMigrationsAsync(cancellationToken));
            var series = await reloaded.StorySeries.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == graph.SeriesId, cancellationToken);
            var batch = await reloaded.SeriesCastRebuildBatches.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == graph.BatchId, cancellationToken);
            var books = await reloaded.SeriesBooks.AsNoTracking()
                .Where(candidate => candidate.SeriesId == graph.SeriesId)
                .OrderBy(candidate => candidate.Id)
                .ToArrayAsync(cancellationToken);
            var jobs = await reloaded.NarrationJobs.AsNoTracking()
                .Where(candidate => graph.JobIds.Contains(candidate.Id))
                .ToArrayAsync(cancellationToken);

            Assert.Null(series.ActiveCastRevisionId);
            Assert.Equal(SeriesCastRebuildBatchStatus.ReadyToActivate, batch.Status);
            Assert.All(books, book => Assert.Null(book.ActiveNarrationJobId));
            Assert.Equal(graph.JobIds.Length, jobs.Length);
            Assert.All(jobs, job =>
            {
                Assert.Equal(NarrationJobStatus.Completed, job.Status);
                Assert.Equal(NarrationArtifactVisibility.Staged, job.Visibility);
                Assert.NotNull(job.AudioRelativePath);
                Assert.True(job.AudioBytes > 0);
            });
        }

        var result = await ActivateAsync(graph, cancellationToken);
        Assert.Equal(graph.SeriesId, result.SeriesId);
        Assert.Equal(graph.BatchId, result.BatchId);
        Assert.Equal(graph.RevisionId, result.ActiveCastRevisionId);
        Assert.Equal(1, result.EpochNumber);
        Assert.Equal(graph.ActivatedAt, result.ActivatedAt);

        await AssertCurrentEpochAsync(
            graph.ConnectionString,
            graph,
            expectedEpoch: 1,
            expectedRevisionStatus: NarrationCastRevisionStatus.Active,
            expectedJobVisibility: NarrationArtifactVisibility.Published,
            cancellationToken);
    }

    [Fact]
    public async Task Legacy_predecessors_retire_and_superseding_activation_installs_epoch_two_atomically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var first = await CreateReadyGraphAsync(
            postgres.GetConnectionString(),
            "epoch-legacy-supersede",
            useLegacyPredecessors: true,
            migration: CurrentMigration,
            cancellationToken);

        var firstResult = await ActivateAsync(first, cancellationToken);
        Assert.Equal(1, firstResult.EpochNumber);

        await using (var firstProof = CreateContext(first.ConnectionString))
        {
            var predecessors = await firstProof.NarrationJobs.AsNoTracking()
                .Where(job => first.PreviousJobIds.Contains(job.Id))
                .ToArrayAsync(cancellationToken);
            Assert.Equal(first.PreviousJobIds.Length, predecessors.Length);
            Assert.All(predecessors, predecessor =>
            {
                Assert.Equal(NarrationMode.SingleVoice, predecessor.Mode);
                Assert.Equal(NarrationJobStatus.Completed, predecessor.Status);
                Assert.Equal(NarrationArtifactVisibility.Historical, predecessor.Visibility);
                Assert.Null(predecessor.SeriesId);
                Assert.Null(predecessor.RebuildBatchId);
            });
        }

        var second = await AddReadyBatchAsync(
            first,
            first.RevisionId,
            first.JobIds,
            revisionNumber: 2,
            prefix: "epoch-legacy-supersede-two",
            cancellationToken);
        var secondActivation = first.ActivatedAt.AddMinutes(1);
        var secondResult = await ActivateAsync(
            first.ConnectionString,
            first.OwnerId,
            first.SeriesId,
            second.BatchId,
            secondActivation,
            cancellationToken);

        Assert.Equal(2, secondResult.EpochNumber);
        Assert.Equal(second.RevisionId, secondResult.ActiveCastRevisionId);

        await using var proof = CreateContext(first.ConnectionString);
        var series = await proof.StorySeries.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == first.SeriesId, cancellationToken);
        var revisions = await proof.NarrationCastRevisions.AsNoTracking()
            .Where(candidate => candidate.SeriesId == first.SeriesId)
            .OrderBy(candidate => candidate.EpochNumber)
            .ToArrayAsync(cancellationToken);
        var batches = await proof.SeriesCastRebuildBatches.AsNoTracking()
            .Where(candidate => candidate.SeriesId == first.SeriesId)
            .OrderBy(candidate => candidate.CreatedAt)
            .ToArrayAsync(cancellationToken);
        var oldJobs = await proof.NarrationJobs.AsNoTracking()
            .Where(candidate => first.JobIds.Contains(candidate.Id))
            .ToArrayAsync(cancellationToken);
        var newJobs = await proof.NarrationJobs.AsNoTracking()
            .Where(candidate => second.JobIds.Contains(candidate.Id))
            .ToArrayAsync(cancellationToken);
        var pointers = await proof.SeriesBooks.AsNoTracking()
            .Where(candidate => candidate.SeriesId == first.SeriesId)
            .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.ActiveNarrationJobId, cancellationToken);

        Assert.Equal(second.RevisionId, series.ActiveCastRevisionId);
        Assert.Collection(
            revisions,
            revision =>
            {
                Assert.Equal(first.RevisionId, revision.Id);
                Assert.Equal(1, revision.EpochNumber);
                Assert.Equal(NarrationCastRevisionStatus.Historical, revision.Status);
            },
            revision =>
            {
                Assert.Equal(second.RevisionId, revision.Id);
                Assert.Equal(2, revision.EpochNumber);
                Assert.Equal(NarrationCastRevisionStatus.Active, revision.Status);
            });
        Assert.Equal(2, batches.Length);
        Assert.All(batches, batch => Assert.Equal(SeriesCastRebuildBatchStatus.Activated, batch.Status));
        Assert.All(oldJobs, job => Assert.Equal(NarrationArtifactVisibility.Historical, job.Visibility));
        Assert.All(newJobs, job => Assert.Equal(NarrationArtifactVisibility.Published, job.Visibility));
        Assert.All(second.Members, member => Assert.Equal(member.JobId, pointers[member.SeriesBookId]));
    }

    [Fact]
    public async Task Validation_failures_are_stable_happen_before_save_and_leave_fresh_state_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var graph = await CreateReadyGraphAsync(
            postgres.GetConnectionString(),
            "epoch-validation",
            useLegacyPredecessors: false,
            migration: CurrentMigration,
            cancellationToken);
        var interceptor = new SaveProbeInterceptor();

        await using (var timestampContext = CreateContext(graph.ConnectionString, interceptor))
        {
            var publisher = new PostgreSqlCastEpochActivationPublisher(timestampContext);
            var exception = await Assert.ThrowsAsync<CastEpochActivationRejectedException>(() =>
                publisher.ActivateAsync(
                    new CastEpochActivationCommand(
                        graph.OwnerId,
                        graph.SeriesId,
                        graph.BatchId,
                        graph.ActivatedAt.AddYears(-10)),
                    cancellationToken));
            Assert.Equal(CastEpochActivationFailure.EpochStateInvalid, exception.Failure);
            Assert.Equal("Cast epoch activation was rejected.", exception.Message);
        }

        Assert.Equal(0, interceptor.SaveCalls);

        var unsafeJobId = graph.JobIds[0];
        await using (var mutate = CreateContext(graph.ConnectionString))
        {
            await mutate.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_jobs SET \"AudioRelativePath\" = '../synthetic-escape.mp3' WHERE \"Id\" = {unsafeJobId}",
                cancellationToken);
        }

        await using (var unsafeContext = CreateContext(graph.ConnectionString, interceptor))
        {
            var publisher = new PostgreSqlCastEpochActivationPublisher(unsafeContext);
            var exception = await Assert.ThrowsAsync<CastEpochActivationRejectedException>(() =>
                publisher.ActivateAsync(
                    new CastEpochActivationCommand(
                        graph.OwnerId,
                        graph.SeriesId,
                        graph.BatchId,
                        graph.ActivatedAt),
                    cancellationToken));
            Assert.Equal(CastEpochActivationFailure.StagedArtifactInvalid, exception.Failure);
            Assert.Equal("Cast epoch activation was rejected.", exception.Message);
        }

        Assert.Equal(0, interceptor.SaveCalls);
        await using var proof = CreateContext(graph.ConnectionString);
        var series = await proof.StorySeries.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.SeriesId, cancellationToken);
        var revision = await proof.NarrationCastRevisions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.RevisionId, cancellationToken);
        var batch = await proof.SeriesCastRebuildBatches.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.BatchId, cancellationToken);
        var unsafeJob = await proof.NarrationJobs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == unsafeJobId, cancellationToken);
        Assert.Null(series.ActiveCastRevisionId);
        Assert.Equal(NarrationCastRevisionStatus.Draft, revision.Status);
        Assert.Null(revision.EpochNumber);
        Assert.Equal(SeriesCastRebuildBatchStatus.ReadyToActivate, batch.Status);
        Assert.Equal(NarrationArtifactVisibility.Staged, unsafeJob.Visibility);
        Assert.Equal("../synthetic-escape.mp3", unsafeJob.AudioRelativePath);
    }

    [Fact]
    public async Task Every_publisher_rejection_category_is_stable_pre_mutation_and_preserves_fresh_database_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var connectionString = postgres.GetConnectionString();
        var validationCases = CreatePublisherValidationCases();

        for (var index = 0; index < validationCases.Length; index++)
        {
            var validationCase = validationCases[index];
            var graph = await CreateValidationGraphAsync(
                connectionString,
                $"strict-{index:D2}",
                validationCase.GraphKind,
                cancellationToken);

            await ApplyValidationMutationAsync(
                validationCase.Mutation,
                graph,
                cancellationToken);
            var before = await CapturePublisherStateAsync(connectionString, cancellationToken);
            var interceptor = new SaveProbeInterceptor();

            try
            {
                await using (var db = CreateContext(connectionString, interceptor))
                {
                    var publisher = new PostgreSqlCastEpochActivationPublisher(db);
                    var exception = await Assert.ThrowsAsync<CastEpochActivationRejectedException>(() =>
                        publisher.ActivateAsync(
                            CreateValidationCommand(validationCase.Command, graph),
                            cancellationToken));

                    Assert.True(
                        exception.Failure == validationCase.ExpectedFailure,
                        $"{validationCase.Name}: expected {validationCase.ExpectedFailure}, got {exception.Failure}.");
                    Assert.Equal("Cast epoch activation was rejected.", exception.Message);
                }

                Assert.True(
                    interceptor.SaveCalls == 0,
                    $"{validationCase.Name}: expected exactly zero SaveChanges calls, got {interceptor.SaveCalls}.");
                var after = await CapturePublisherStateAsync(connectionString, cancellationToken);
                Assert.True(
                    string.Equals(before, after, StringComparison.Ordinal),
                    $"{validationCase.Name}: publisher rejection changed persisted state.");
            }
            finally
            {
                await CleanupValidationMutationAsync(
                    validationCase.Mutation,
                    graph,
                    cancellationToken);
            }
        }
    }

    [Fact]
    public async Task Injected_stage_two_save_failure_rolls_back_stage_one_retirement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var graph = await CreateReadyGraphAsync(
            postgres.GetConnectionString(),
            "epoch-stage-two-rollback",
            useLegacyPredecessors: true,
            migration: CurrentMigration,
            cancellationToken);
        var interceptor = new SaveProbeInterceptor(throwOnSaveCall: 2);

        await using (var failingContext = CreateContext(graph.ConnectionString, interceptor))
        {
            var publisher = new PostgreSqlCastEpochActivationPublisher(failingContext);
            await Assert.ThrowsAsync<InjectedStageTwoException>(() =>
                publisher.ActivateAsync(
                    new CastEpochActivationCommand(
                        graph.OwnerId,
                        graph.SeriesId,
                        graph.BatchId,
                        graph.ActivatedAt),
                    cancellationToken));
            Assert.Empty(failingContext.ChangeTracker.Entries());
        }

        Assert.Equal(2, interceptor.SaveCalls);
        await using var proof = CreateContext(graph.ConnectionString);
        var series = await proof.StorySeries.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.SeriesId, cancellationToken);
        var books = await proof.SeriesBooks.AsNoTracking()
            .Where(candidate => candidate.SeriesId == graph.SeriesId)
            .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.ActiveNarrationJobId, cancellationToken);
        var revision = await proof.NarrationCastRevisions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.RevisionId, cancellationToken);
        var batch = await proof.SeriesCastRebuildBatches.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.BatchId, cancellationToken);
        var staged = await proof.NarrationJobs.AsNoTracking()
            .Where(candidate => graph.JobIds.Contains(candidate.Id))
            .ToArrayAsync(cancellationToken);
        var predecessors = await proof.NarrationJobs.AsNoTracking()
            .Where(candidate => graph.PreviousJobIds.Contains(candidate.Id))
            .ToArrayAsync(cancellationToken);

        Assert.Null(series.ActiveCastRevisionId);
        Assert.Equal(NarrationCastRevisionStatus.Draft, revision.Status);
        Assert.Null(revision.EpochNumber);
        Assert.Equal(SeriesCastRebuildBatchStatus.ReadyToActivate, batch.Status);
        Assert.All(staged, job => Assert.Equal(NarrationArtifactVisibility.Staged, job.Visibility));
        Assert.All(predecessors, job => Assert.Equal(NarrationArtifactVisibility.Published, job.Visibility));
        Assert.All(graph.Members, member => Assert.Equal(member.PreviousJobId, books[member.SeriesBookId]));
    }

    [Fact]
    public async Task Concurrent_same_base_publishers_produce_one_epoch_two_and_one_stale_loser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var first = await CreateReadyGraphAsync(
            postgres.GetConnectionString(),
            "epoch-concurrent",
            useLegacyPredecessors: false,
            migration: CurrentMigration,
            cancellationToken);
        await ActivateAsync(first, cancellationToken);

        var contenderA = await AddReadyBatchAsync(
            first,
            first.RevisionId,
            first.JobIds,
            revisionNumber: 2,
            prefix: "epoch-concurrent-a",
            cancellationToken);
        var contenderB = await AddReadyBatchAsync(
            first,
            first.RevisionId,
            first.JobIds,
            revisionNumber: 3,
            prefix: "epoch-concurrent-b",
            cancellationToken);
        var activatedAt = first.ActivatedAt.AddMinutes(1);

        var outcomes = await Task.WhenAll(
            RunActivationAsync(first.ConnectionString, first, contenderA.BatchId, activatedAt, cancellationToken),
            RunActivationAsync(first.ConnectionString, first, contenderB.BatchId, activatedAt, cancellationToken));

        var winner = Assert.Single(outcomes, outcome => outcome.Result is not null);
        var loser = Assert.Single(outcomes, outcome => outcome.Exception is not null);
        Assert.Equal(2, winner.Result!.EpochNumber);
        var rejection = Assert.IsType<CastEpochActivationRejectedException>(loser.Exception);
        Assert.Equal(CastEpochActivationFailure.StaleBaseRevision, rejection.Failure);

        await using var proof = CreateContext(first.ConnectionString);
        var revisions = await proof.NarrationCastRevisions.AsNoTracking()
            .Where(candidate => candidate.SeriesId == first.SeriesId)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, revisions.Count(revision => revision.EpochNumber.HasValue));
        Assert.Single(revisions, revision => revision.EpochNumber == 1 && revision.Status == NarrationCastRevisionStatus.Historical);
        Assert.Single(revisions, revision => revision.EpochNumber == 2 && revision.Status == NarrationCastRevisionStatus.Active);
        Assert.DoesNotContain(revisions, revision => revision.EpochNumber == 3);
        Assert.Single(revisions, revision => revision.Status == NarrationCastRevisionStatus.Draft);

        var batches = await proof.SeriesCastRebuildBatches.AsNoTracking()
            .Where(candidate => candidate.SeriesId == first.SeriesId)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, batches.Count(batch => batch.Status == SeriesCastRebuildBatchStatus.Activated));
        Assert.Single(batches, batch => batch.Status == SeriesCastRebuildBatchStatus.ReadyToActivate);

        var activeSeries = await proof.StorySeries.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == first.SeriesId, cancellationToken);
        Assert.Equal(winner.Result.ActiveCastRevisionId, activeSeries.ActiveCastRevisionId);
    }

    [Fact]
    public async Task Named_foreign_keys_unique_index_and_every_integrity_branch_reject_isolated_invalid_graphs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var connectionString = postgres.GetConnectionString();

        var current = await CreateReadyGraphAsync(
            connectionString,
            "epoch-constraints-current",
            useLegacyPredecessors: false,
            migration: CurrentMigration,
            cancellationToken);
        await ActivateAsync(current, cancellationToken);

        var legacy = await CreateReadyGraphAsync(
            connectionString,
            "epoch-constraints-legacy",
            useLegacyPredecessors: true,
            migration: CurrentMigration,
            cancellationToken);
        await ActivateAsync(legacy, cancellationToken);

        var superseded = await CreateReadyGraphAsync(
            connectionString,
            "epoch-constraints-superseded",
            useLegacyPredecessors: false,
            migration: CurrentMigration,
            cancellationToken);
        await ActivateAsync(superseded, cancellationToken);
        var supersedingBatch = await AddReadyBatchAsync(
            superseded,
            superseded.RevisionId,
            superseded.JobIds,
            revisionNumber: 2,
            prefix: "epoch-constraints-superseding-two",
            cancellationToken);
        await ActivateAsync(
            connectionString,
            superseded.OwnerId,
            superseded.SeriesId,
            supersedingBatch.BatchId,
            superseded.ActivatedAt.AddMinutes(1),
            cancellationToken);

        var preactivation = await CreateReadyGraphAsync(
            connectionString,
            "epoch-constraints-preactivation",
            useLegacyPredecessors: false,
            migration: CurrentMigration,
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE story_series SET \"ActiveCastRevisionId\" = {preactivation.RevisionId} WHERE \"Id\" = {current.SeriesId}",
                cancellationToken),
            PostgresErrorCodes.ForeignKeyViolation,
            "FK_story_series_active_cast",
            async db => Assert.Equal(
                current.RevisionId,
                (await db.StorySeries.AsNoTracking().SingleAsync(series => series.Id == current.SeriesId, cancellationToken)).ActiveCastRevisionId),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE series_books SET \"ActiveNarrationJobId\" = {preactivation.JobIds[0]} WHERE \"Id\" = {current.Members[0].SeriesBookId}",
                cancellationToken),
            PostgresErrorCodes.ForeignKeyViolation,
            "FK_series_books_active_job",
            async db => Assert.Equal(
                current.Members[0].JobId,
                (await db.SeriesBooks.AsNoTracking().SingleAsync(book => book.Id == current.Members[0].SeriesBookId, cancellationToken)).ActiveNarrationJobId),
            cancellationToken);

        await using (var duplicate = CreateContext(connectionString))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                duplicate.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO series_cast_rebuild_batches
                        ("Id", "OwnerId", "SeriesId", "BaseActiveCastRevisionId", "DraftCastRevisionId",
                         "CohortMembershipRevision", "Status", "CreatedAt", "UpdatedAt")
                    SELECT {Guid.NewGuid()}, "OwnerId", "SeriesId", "BaseActiveCastRevisionId", "DraftCastRevisionId",
                           "CohortMembershipRevision", 'Draft', "CreatedAt", "UpdatedAt"
                    FROM series_cast_rebuild_batches
                    WHERE "Id" = {preactivation.BatchId};
                    """, cancellationToken));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
            Assert.Equal("UX_rebuild_batches_draft_cast", exception.ConstraintName);
        }

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE series_cast_rebuild_batches SET \"Status\" = 'ReadyToActivate' WHERE \"Id\" = {current.BatchId}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_cast_epoch_active_pointer",
            async db => Assert.Equal(
                SeriesCastRebuildBatchStatus.Activated,
                (await db.SeriesCastRebuildBatches.AsNoTracking().SingleAsync(batch => batch.Id == current.BatchId, cancellationToken)).Status),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_cast_revisions SET \"Status\" = 'Historical' WHERE \"Id\" = {current.RevisionId}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_cast_epoch_revision_state",
            async db => Assert.Equal(
                NarrationCastRevisionStatus.Active,
                (await db.NarrationCastRevisions.AsNoTracking().SingleAsync(revision => revision.Id == current.RevisionId, cancellationToken)).Status),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_cast_revisions SET \"EpochNumber\" = 2 WHERE \"Id\" = {current.RevisionId}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_cast_epoch_batch_chain",
            async db => Assert.Equal(
                1,
                (await db.NarrationCastRevisions.AsNoTracking().SingleAsync(revision => revision.Id == current.RevisionId, cancellationToken)).EpochNumber),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE series_cast_rebuild_batches SET \"CohortMembershipRevision\" = \"CohortMembershipRevision\" + 1 WHERE \"Id\" = {current.BatchId}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_cast_epoch_full_cohort",
            async db => Assert.Equal(
                current.Members.Length,
                (await db.SeriesCastRebuildBatches.AsNoTracking().SingleAsync(batch => batch.Id == current.BatchId, cancellationToken)).CohortMembershipRevision),
            cancellationToken);

        var omittedActivationMember = preactivation.Members[0];
        var retainedActivationMember = preactivation.Members[1];
        await AssertDeferredFailureAsync(
            connectionString,
            async db =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM narration_jobs WHERE \"Id\" = {omittedActivationMember.JobId}",
                    cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM series_cast_rebuild_members WHERE \"Id\" = {omittedActivationMember.MemberId}",
                    cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE narration_jobs SET \"Visibility\" = 'Published' WHERE \"Id\" = {retainedActivationMember.JobId}",
                    cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE series_books SET \"ActiveNarrationJobId\" = {retainedActivationMember.JobId} WHERE \"Id\" = {retainedActivationMember.SeriesBookId}",
                    cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE narration_cast_revisions SET \"Status\" = 'Active', \"EpochNumber\" = 1, \"ActivatedAt\" = {preactivation.ActivatedAt} WHERE \"Id\" = {preactivation.RevisionId}",
                    cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE story_series SET \"ActiveCastRevisionId\" = {preactivation.RevisionId} WHERE \"Id\" = {preactivation.SeriesId}",
                    cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE series_cast_rebuild_batches SET \"Status\" = 'Activated', \"UpdatedAt\" = {preactivation.ActivatedAt} WHERE \"Id\" = {preactivation.BatchId}",
                    cancellationToken);
            },
            PostgresErrorCodes.CheckViolation,
            "CK_cast_epoch_full_cohort",
            async db =>
            {
                var batch = await db.SeriesCastRebuildBatches.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == preactivation.BatchId, cancellationToken);
                var revision = await db.NarrationCastRevisions.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == preactivation.RevisionId, cancellationToken);
                var series = await db.StorySeries.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == preactivation.SeriesId, cancellationToken);
                var books = await db.SeriesBooks.AsNoTracking()
                    .Where(candidate => candidate.SeriesId == preactivation.SeriesId)
                    .ToArrayAsync(cancellationToken);

                Assert.Equal(SeriesCastRebuildBatchStatus.ReadyToActivate, batch.Status);
                Assert.Equal(NarrationCastRevisionStatus.Draft, revision.Status);
                Assert.Null(series.ActiveCastRevisionId);
                Assert.Equal(preactivation.Members.Length, await db.SeriesCastRebuildMembers.AsNoTracking()
                    .CountAsync(member => member.BatchId == preactivation.BatchId, cancellationToken));
                Assert.Equal(preactivation.JobIds.Length, await db.NarrationJobs.AsNoTracking()
                    .CountAsync(job => preactivation.JobIds.Contains(job.Id), cancellationToken));
                Assert.All(books, book => Assert.Null(book.ActiveNarrationJobId));
            },
            cancellationToken);

        await using (var upgrade = CreateContext(connectionString))
        {
            await upgrade.Database.ExecuteSqlRawAsync(
                PendingSeriesBookOnboardingSql.Up,
                Array.Empty<object>(),
                cancellationToken);
        }

        var pendingBook = Book.Create(
            current.OwnerId,
            "pending-onboarding synthetic book",
            "Synthetic author",
            "en",
            "pending-onboarding.txt");
        await using (var pendingBookSetup = CreateContext(connectionString))
        {
            pendingBookSetup.Books.Add(pendingBook);
            await pendingBookSetup.SaveChangesAsync(cancellationToken);
        }

        var pendingSeriesBookId = Guid.NewGuid();
        var pendingVolumeLabel = "Pending onboarding synthetic volume";
        await using (var addPendingMembership = CreateContext(connectionString))
        {
            await using var transaction = await addPendingMembership.Database.BeginTransactionAsync(cancellationToken);
            await addPendingMembership.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO series_books
                    ("Id", "OwnerId", "SeriesId", "BookId", "VolumeLabel", "SortOrder",
                     "MembershipRevision", "ActiveNarrationJobId")
                VALUES
                    ({pendingSeriesBookId}, {current.OwnerId}, {current.SeriesId}, {pendingBook.Id},
                     {pendingVolumeLabel}, 1000, {current.Members.Length + 1}, NULL)
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using (var pendingProof = CreateContext(connectionString))
        {
            var pendingMembership = await pendingProof.SeriesBooks.AsNoTracking().SingleAsync(
                book => book.Id == pendingSeriesBookId,
                cancellationToken);
            Assert.Equal(pendingBook.Id, pendingMembership.BookId);
            Assert.Equal(current.Members.Length + 1, pendingMembership.MembershipRevision);
            Assert.Null(pendingMembership.ActiveNarrationJobId);
        }
        await AssertCurrentEpochAsync(
            connectionString,
            current,
            expectedEpoch: 1,
            expectedRevisionStatus: NarrationCastRevisionStatus.Active,
            expectedJobVisibility: NarrationArtifactVisibility.Published,
            cancellationToken);

        await using (var downGuard = CreateContext(connectionString))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                downGuard.Database.ExecuteSqlRawAsync(
                    PendingSeriesBookOnboardingSql.Down,
                    Array.Empty<object>(),
                    cancellationToken));
            Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
            Assert.Equal(
                "Cannot roll back pending series-book onboarding while an active epoch has pending memberships.",
                exception.MessageText);
        }

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE series_cast_rebuild_members SET \"Status\" = 'Building' WHERE \"Id\" = {current.Members[0].MemberId}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_cast_epoch_member_state",
            async db => Assert.Equal(
                SeriesCastRebuildMemberStatus.Ready,
                (await db.SeriesCastRebuildMembers.AsNoTracking().SingleAsync(member => member.Id == current.Members[0].MemberId, cancellationToken)).Status),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE series_books SET \"ActiveNarrationJobId\" = NULL WHERE \"Id\" = {current.Members[0].SeriesBookId}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_cast_epoch_current_pointer",
            async db => Assert.Equal(
                current.Members[0].JobId,
                (await db.SeriesBooks.AsNoTracking().SingleAsync(book => book.Id == current.Members[0].SeriesBookId, cancellationToken)).ActiveNarrationJobId),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            async db =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE series_cast_rebuild_batches SET \"Status\" = 'Building' WHERE \"Id\" = {preactivation.BatchId}",
                    cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE series_cast_rebuild_members SET \"Status\" = 'Pending', \"StagedNarrationJobId\" = NULL WHERE \"Id\" = {preactivation.Members[0].MemberId}",
                    cancellationToken);
            },
            PostgresErrorCodes.CheckViolation,
            "CK_rebuild_artifact_membership",
            async db => Assert.Equal(
                preactivation.Members[0].JobId,
                (await db.SeriesCastRebuildMembers.AsNoTracking().SingleAsync(member => member.Id == preactivation.Members[0].MemberId, cancellationToken)).StagedNarrationJobId),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_jobs SET \"Visibility\" = 'Historical' WHERE \"Id\" = {current.JobIds[0]}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_rebuild_artifact_visibility",
            async db => Assert.Equal(
                NarrationArtifactVisibility.Published,
                (await db.NarrationJobs.AsNoTracking().SingleAsync(job => job.Id == current.JobIds[0], cancellationToken)).Visibility),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_jobs SET \"Visibility\" = 'Published' WHERE \"Id\" = {superseded.JobIds[0]}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_rebuild_artifact_visibility",
            async db => Assert.Equal(
                NarrationArtifactVisibility.Historical,
                (await db.NarrationJobs.AsNoTracking().SingleAsync(job => job.Id == superseded.JobIds[0], cancellationToken)).Visibility),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_jobs SET \"Visibility\" = 'Published' WHERE \"Id\" = {preactivation.JobIds[0]}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_rebuild_artifact_visibility",
            async db => Assert.Equal(
                NarrationArtifactVisibility.Staged,
                (await db.NarrationJobs.AsNoTracking().SingleAsync(job => job.Id == preactivation.JobIds[0], cancellationToken)).Visibility),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_jobs SET \"AudioRelativePath\" = '../unsafe-current.mp3' WHERE \"Id\" = {current.JobIds[0]}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_rebuild_artifact_visibility",
            async db => Assert.DoesNotContain(
                "..",
                (await db.NarrationJobs.AsNoTracking().SingleAsync(job => job.Id == current.JobIds[0], cancellationToken)).AudioRelativePath,
                StringComparison.Ordinal),
            cancellationToken);

        var legacyPredecessorId = legacy.PreviousJobIds[0];
        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_jobs SET \"Visibility\" = 'Published' WHERE \"Id\" = {legacyPredecessorId}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_cast_epoch_previous_artifact",
            async db => Assert.Equal(
                NarrationArtifactVisibility.Historical,
                (await db.NarrationJobs.AsNoTracking().SingleAsync(job => job.Id == legacyPredecessorId, cancellationToken)).Visibility),
            cancellationToken);

        await AssertDeferredFailureAsync(
            connectionString,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_jobs SET \"AudioBytes\" = NULL WHERE \"Id\" = {legacyPredecessorId}",
                cancellationToken),
            PostgresErrorCodes.CheckViolation,
            "CK_cast_epoch_previous_artifact",
            async db => Assert.True(
                (await db.NarrationJobs.AsNoTracking().SingleAsync(job => job.Id == legacyPredecessorId, cancellationToken)).AudioBytes > 0),
            cancellationToken);
    }

    [Fact]
    public async Task Pending_onboarding_down_restores_the_full_cohort_fence_when_no_pending_membership_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var current = await CreateReadyGraphAsync(
            postgres.GetConnectionString(),
            "epoch-pending-onboarding-down",
            useLegacyPredecessors: false,
            migration: CurrentMigration,
            cancellationToken);
        await ActivateAsync(current, cancellationToken);

        await using (var migration = CreateContext(current.ConnectionString))
        {
            await migration.Database.ExecuteSqlRawAsync(
                PendingSeriesBookOnboardingSql.Up,
                Array.Empty<object>(),
                cancellationToken);
            await migration.Database.ExecuteSqlRawAsync(
                PendingSeriesBookOnboardingSql.Down,
                Array.Empty<object>(),
                cancellationToken);
        }

        await using (var definitionProof = CreateContext(current.ConnectionString))
        {
            var definition = await definitionProof.Database.SqlQueryRaw<string>(
                """
                SELECT pg_get_functiondef(procedure.oid) AS "Value"
                FROM pg_proc AS procedure
                INNER JOIN pg_namespace AS schema_name ON schema_name.oid = procedure.pronamespace
                WHERE procedure.proname = 'assert_cast_epoch_integrity'
                    AND schema_name.nspname = 'public'
                """)
                .SingleAsync(cancellationToken);
            Assert.DoesNotContain(
                "series_book.\"MembershipRevision\" <= batch.\"CohortMembershipRevision\"",
                definition,
                StringComparison.Ordinal);
        }

        var pendingBook = Book.Create(
            current.OwnerId,
            "rollback-fence synthetic book",
            "Synthetic author",
            "en",
            "rollback-fence.txt");
        await using (var setup = CreateContext(current.ConnectionString))
        {
            setup.Books.Add(pendingBook);
            await setup.SaveChangesAsync(cancellationToken);
        }

        await using var proof = CreateContext(current.ConnectionString);
        await using var transaction = await proof.Database.BeginTransactionAsync(cancellationToken);
        await proof.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO series_books
                ("Id", "OwnerId", "SeriesId", "BookId", "VolumeLabel", "SortOrder",
                 "MembershipRevision", "ActiveNarrationJobId")
            VALUES
                ({Guid.NewGuid()}, {current.OwnerId}, {current.SeriesId}, {pendingBook.Id},
                 'rollback fence', 1000, {current.Members.Length + 1}, NULL)
            """, cancellationToken);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync(cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_cast_epoch_full_cohort", exception.ConstraintName);
    }

    [Fact]
    public async Task Deferred_triggers_allow_invalid_intermediate_order_when_final_graph_is_valid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var graph = await CreateReadyGraphAsync(
            postgres.GetConnectionString(),
            "epoch-deferred-order",
            useLegacyPredecessors: false,
            migration: CurrentMigration,
            cancellationToken);

        await using (var db = CreateContext(graph.ConnectionString))
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE series_cast_rebuild_batches SET \"Status\" = 'Activated', \"UpdatedAt\" = {graph.ActivatedAt} WHERE \"Id\" = {graph.BatchId}",
                cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_jobs SET \"Visibility\" = 'Published', \"UpdatedAt\" = {graph.ActivatedAt} WHERE \"RebuildBatchId\" = {graph.BatchId}",
                cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE story_series SET \"ActiveCastRevisionId\" = {graph.RevisionId}, \"UpdatedAt\" = {graph.ActivatedAt}, \"ConcurrencyStamp\" = {Guid.NewGuid()} WHERE \"Id\" = {graph.SeriesId}",
                cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE narration_cast_revisions SET \"Status\" = 'Active', \"EpochNumber\" = 1, \"ActivatedAt\" = {graph.ActivatedAt} WHERE \"Id\" = {graph.RevisionId}",
                cancellationToken);
            foreach (var member in graph.Members)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE series_books SET \"ActiveNarrationJobId\" = {member.JobId} WHERE \"Id\" = {member.SeriesBookId}",
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await AssertCurrentEpochAsync(
            graph.ConnectionString,
            graph,
            expectedEpoch: 1,
            expectedRevisionStatus: NarrationCastRevisionStatus.Active,
            expectedJobVisibility: NarrationArtifactVisibility.Published,
            cancellationToken);
    }

    [Fact]
    public async Task Down_guard_is_atomic_and_empty_old_phase_can_down_and_reupgrade_safely()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await StartPostgresAsync(cancellationToken);
        var graph = await CreateReadyGraphAsync(
            postgres.GetConnectionString(),
            "epoch-down-guard",
            useLegacyPredecessors: false,
            migration: CurrentMigration,
            cancellationToken);
        await ActivateAsync(graph, cancellationToken);

        await using (var guarded = CreateContext(graph.ConnectionString))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                guarded.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken));
            Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
            Assert.Equal(
                "Cannot roll back atomic cast epoch activation while active pointers or activated artifacts exist.",
                exception.MessageText);
        }

        await using (var proof = CreateContext(graph.ConnectionString))
        {
            Assert.Contains(CurrentMigration, await proof.Database.GetAppliedMigrationsAsync(cancellationToken));
            Assert.Equal(
                graph.RevisionId,
                (await proof.StorySeries.AsNoTracking().SingleAsync(series => series.Id == graph.SeriesId, cancellationToken)).ActiveCastRevisionId);
            Assert.Equal(
                SeriesCastRebuildBatchStatus.Activated,
                (await proof.SeriesCastRebuildBatches.AsNoTracking().SingleAsync(batch => batch.Id == graph.BatchId, cancellationToken)).Status);
        }

        var safeConnectionString = await CreateDatabaseAsync(
            postgres.GetConnectionString(),
            $"storyvoice_epoch_safe_{Guid.NewGuid():N}",
            cancellationToken);
        await using (var safe = CreateContext(safeConnectionString))
        {
            var migrator = safe.GetService<IMigrator>();
            await migrator.MigrateAsync(CurrentMigration, cancellationToken);
            await migrator.MigrateAsync(PreviousMigration, cancellationToken);
            Assert.DoesNotContain(CurrentMigration, await safe.Database.GetAppliedMigrationsAsync(cancellationToken));
            Assert.True(await TriggerExistsAsync(
                safeConnectionString,
                "CT_rebuild_artifact_member",
                cancellationToken));
            await migrator.MigrateAsync(CurrentMigration, cancellationToken);
            Assert.Contains(CurrentMigration, await safe.Database.GetAppliedMigrationsAsync(cancellationToken));
            Assert.True(await TriggerExistsAsync(
                safeConnectionString,
                "CT_cast_epoch_series",
                cancellationToken));
        }
    }

    private static PublisherValidationCase[] CreatePublisherValidationCases() =>
    [
        new(
            "wrong-command-owner",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.None,
            PublisherValidationCommand.WrongOwner,
            CastEpochActivationFailure.ScopeNotFound),
        new(
            "wrong-command-series",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.None,
            PublisherValidationCommand.WrongSeries,
            CastEpochActivationFailure.ScopeNotFound),
        new(
            "missing-batch",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.None,
            PublisherValidationCommand.MissingBatch,
            CastEpochActivationFailure.ScopeNotFound),
        new(
            "batch-not-ready",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.BatchNotReady,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.BatchNotReady),
        new(
            "stale-base-pointer",
            ValidationGraphKind.Superseding,
            PublisherValidationMutation.StaleBasePointer,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StaleBaseRevision),
        new(
            "draft-wrong-id",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.DraftWrongId,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.DraftRevisionInvalid),
        new(
            "draft-wrong-scope",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.DraftWrongScope,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.DraftRevisionInvalid),
        new(
            "draft-wrong-state",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.DraftWrongState,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.DraftRevisionInvalid),
        new(
            "base-wrong-state",
            ValidationGraphKind.Superseding,
            PublisherValidationMutation.BaseWrongState,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.BaseRevisionInvalid),
        new(
            "new-series-book-after-snapshot",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.NewSeriesBook,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.CohortChanged),
        new(
            "missing-series-book-after-snapshot",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.MissingSeriesBook,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.CohortChanged),
        new(
            "stale-membership-tuple",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.StaleMembershipTuple,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.CohortChanged),
        new(
            "missing-member",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.MissingMember,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.CohortChanged),
        new(
            "extra-member",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.ExtraMember,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.CohortChanged),
        new(
            "member-not-ready",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.MemberNotReady,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.MemberInvalid),
        new(
            "null-staged-pointer",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.NullStagedPointer,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.MemberInvalid),
        new(
            "wrong-staged-pointer",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.WrongStagedPointer,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-wrong-owner",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobWrongOwner,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-wrong-series",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobWrongSeries,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-wrong-batch",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobWrongBatch,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-wrong-member",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobWrongMember,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-wrong-book",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobWrongBook,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-wrong-cast",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobWrongCast,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-not-completed",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobNotCompleted,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-already-published",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobPublished,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-already-historical",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobHistorical,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-zero-bytes",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobZeroBytes,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-rooted-path",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobRootedPath,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-drive-letter-path",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobDriveLetterPath,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "staged-job-dot-segment-path",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.JobDotSegmentPath,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.StagedArtifactInvalid),
        new(
            "previous-pointer-changed",
            ValidationGraphKind.LegacyPredecessors,
            PublisherValidationMutation.PreviousPointerChanged,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.PreviousPointerChanged),
        new(
            "previous-job-missing",
            ValidationGraphKind.LegacyPredecessors,
            PublisherValidationMutation.PreviousJobMissing,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.PreviousArtifactInvalid),
        new(
            "previous-job-wrong-book",
            ValidationGraphKind.LegacyPredecessors,
            PublisherValidationMutation.PreviousJobWrongBook,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.PreviousArtifactInvalid),
        new(
            "previous-job-not-published",
            ValidationGraphKind.LegacyPredecessors,
            PublisherValidationMutation.PreviousJobNotPublished,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.PreviousArtifactInvalid),
        new(
            "previous-job-incomplete",
            ValidationGraphKind.LegacyPredecessors,
            PublisherValidationMutation.PreviousJobIncomplete,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.PreviousArtifactInvalid),
        new(
            "previous-job-unsafe",
            ValidationGraphKind.LegacyPredecessors,
            PublisherValidationMutation.PreviousJobUnsafe,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.PreviousArtifactInvalid),
        new(
            "inconsistent-existing-epoch-chain",
            ValidationGraphKind.Superseding,
            PublisherValidationMutation.InconsistentEpochChain,
            PublisherValidationCommand.Default,
            CastEpochActivationFailure.EpochStateInvalid),
        new(
            "activation-timestamp-regression",
            ValidationGraphKind.FirstActivation,
            PublisherValidationMutation.None,
            PublisherValidationCommand.TimestampRegression,
            CastEpochActivationFailure.EpochStateInvalid)
    ];

    private static async Task<ReadyGraph> CreateValidationGraphAsync(
        string connectionString,
        string prefix,
        ValidationGraphKind graphKind,
        CancellationToken cancellationToken)
    {
        var first = await CreateReadyGraphAsync(
            connectionString,
            prefix,
            useLegacyPredecessors: graphKind == ValidationGraphKind.LegacyPredecessors,
            migration: CurrentMigration,
            cancellationToken);
        if (graphKind != ValidationGraphKind.Superseding)
        {
            return first;
        }

        await ActivateAsync(first, cancellationToken);
        var next = await AddReadyBatchAsync(
            first,
            first.RevisionId,
            first.JobIds,
            revisionNumber: 2,
            prefix: $"{prefix}-next",
            cancellationToken);
        return new ReadyGraph(
            connectionString,
            first.OwnerId,
            first.SeriesId,
            next.RevisionId,
            next.BatchId,
            next.Members,
            first.JobIds,
            first.ActivatedAt.AddMinutes(1));
    }

    private static CastEpochActivationCommand CreateValidationCommand(
        PublisherValidationCommand command,
        ReadyGraph graph) =>
        command switch
        {
            PublisherValidationCommand.WrongOwner => new(
                Guid.NewGuid(),
                graph.SeriesId,
                graph.BatchId,
                graph.ActivatedAt),
            PublisherValidationCommand.WrongSeries => new(
                graph.OwnerId,
                Guid.NewGuid(),
                graph.BatchId,
                graph.ActivatedAt),
            PublisherValidationCommand.MissingBatch => new(
                graph.OwnerId,
                graph.SeriesId,
                Guid.NewGuid(),
                graph.ActivatedAt),
            PublisherValidationCommand.TimestampRegression => new(
                graph.OwnerId,
                graph.SeriesId,
                graph.BatchId,
                graph.ActivatedAt.AddYears(-10)),
            _ => new(
                graph.OwnerId,
                graph.SeriesId,
                graph.BatchId,
                graph.ActivatedAt)
        };

    private static async Task ApplyValidationMutationAsync(
        PublisherValidationMutation mutation,
        ReadyGraph graph,
        CancellationToken cancellationToken)
    {
        var firstMember = graph.Members[0];
        var firstJobId = firstMember.JobId;
        var firstPreviousJobId = firstMember.PreviousJobId ?? Guid.Empty;

        switch (mutation)
        {
            case PublisherValidationMutation.None:
                return;
            case PublisherValidationMutation.BatchNotReady:
                await ExecuteSqlAsync(
                    graph.ConnectionString,
                    $"UPDATE series_cast_rebuild_batches SET \"Status\" = 'Building' WHERE \"Id\" = {graph.BatchId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.StaleBasePointer:
                await ExecuteSqlAsync(
                    graph.ConnectionString,
                    $"UPDATE series_cast_rebuild_batches SET \"BaseActiveCastRevisionId\" = NULL WHERE \"Id\" = {graph.BatchId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.DraftWrongId:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "series_cast_rebuild_batches",
                    $"UPDATE series_cast_rebuild_batches SET \"DraftCastRevisionId\" = {Guid.NewGuid()} WHERE \"Id\" = {graph.BatchId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.DraftWrongScope:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "narration_cast_revisions",
                    $"UPDATE narration_cast_revisions SET \"SeriesId\" = {Guid.NewGuid()} WHERE \"Id\" = {graph.RevisionId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.DraftWrongState:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "narration_cast_revisions",
                    $"UPDATE narration_cast_revisions SET \"Status\" = 'Active', \"EpochNumber\" = 1, \"ActivatedAt\" = {graph.ActivatedAt} WHERE \"Id\" = {graph.RevisionId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.BaseWrongState:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "narration_cast_revisions",
                    $"""
                    UPDATE narration_cast_revisions
                    SET "Status" = 'Historical'
                    WHERE "Id" = (
                        SELECT "BaseActiveCastRevisionId"
                        FROM series_cast_rebuild_batches
                        WHERE "Id" = {graph.BatchId})
                    """,
                    cancellationToken);
                return;
            case PublisherValidationMutation.NewSeriesBook:
                await AddSyntheticSeriesBookAsync(graph, cancellationToken);
                return;
            case PublisherValidationMutation.MissingSeriesBook:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "series_books",
                    $"DELETE FROM series_books WHERE \"Id\" = {firstMember.SeriesBookId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.StaleMembershipTuple:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "series_cast_rebuild_members",
                    $"UPDATE series_cast_rebuild_members SET \"MembershipRevision\" = \"MembershipRevision\" + 100 WHERE \"Id\" = {firstMember.MemberId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.MissingMember:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "series_cast_rebuild_members",
                    $"DELETE FROM series_cast_rebuild_members WHERE \"Id\" = {firstMember.MemberId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.ExtraMember:
                await AddSyntheticExtraMemberAsync(graph, cancellationToken);
                return;
            case PublisherValidationMutation.MemberNotReady:
                await ExecuteSqlAsync(
                    graph.ConnectionString,
                    $"UPDATE series_cast_rebuild_members SET \"Status\" = 'Building' WHERE \"Id\" = {firstMember.MemberId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.NullStagedPointer:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "series_cast_rebuild_members",
                    $"UPDATE series_cast_rebuild_members SET \"Status\" = 'Failed', \"StagedNarrationJobId\" = NULL WHERE \"Id\" = {firstMember.MemberId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.WrongStagedPointer:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "series_cast_rebuild_members",
                    $"UPDATE series_cast_rebuild_members SET \"StagedNarrationJobId\" = {Guid.NewGuid()} WHERE \"Id\" = {firstMember.MemberId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.JobWrongOwner:
                await CorruptJobAsync(graph, $"\"OwnerId\" = {Guid.NewGuid()}", cancellationToken);
                return;
            case PublisherValidationMutation.JobWrongSeries:
                await CorruptJobAsync(graph, $"\"SeriesId\" = {Guid.NewGuid()}", cancellationToken);
                return;
            case PublisherValidationMutation.JobWrongBatch:
                await CorruptJobAsync(graph, $"\"RebuildBatchId\" = {Guid.NewGuid()}", cancellationToken);
                return;
            case PublisherValidationMutation.JobWrongMember:
                await CorruptJobAsync(graph, $"\"RebuildMemberId\" = {Guid.NewGuid()}", cancellationToken);
                return;
            case PublisherValidationMutation.JobWrongBook:
                await CorruptJobAsync(graph, $"\"BookId\" = {graph.Members[1].BookId}", cancellationToken);
                return;
            case PublisherValidationMutation.JobWrongCast:
                await CorruptJobAsync(graph, $"\"CastRevisionId\" = {Guid.NewGuid()}", cancellationToken);
                return;
            case PublisherValidationMutation.JobNotCompleted:
                await ExecuteSqlAsync(
                    graph.ConnectionString,
                    $"UPDATE narration_jobs SET \"Status\" = 'Running' WHERE \"Id\" = {firstJobId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.JobPublished:
                await CorruptJobAsync(
                    graph,
                    $"\"Visibility\" = {NarrationArtifactVisibility.Published.ToString()}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.JobHistorical:
                await CorruptJobAsync(
                    graph,
                    $"\"Visibility\" = {NarrationArtifactVisibility.Historical.ToString()}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.JobZeroBytes:
                await CorruptAudioBytesToZeroAsync(graph, cancellationToken);
                return;
            case PublisherValidationMutation.JobRootedPath:
                await SetJobPathAsync(graph, "/synthetic/rooted.mp3", cancellationToken);
                return;
            case PublisherValidationMutation.JobDriveLetterPath:
                await SetJobPathAsync(graph, @"C:\synthetic\drive.mp3", cancellationToken);
                return;
            case PublisherValidationMutation.JobDotSegmentPath:
                await SetJobPathAsync(graph, "staged/../synthetic.mp3", cancellationToken);
                return;
            case PublisherValidationMutation.PreviousPointerChanged:
                await ExecuteSqlAsync(
                    graph.ConnectionString,
                    $"UPDATE series_books SET \"ActiveNarrationJobId\" = NULL WHERE \"Id\" = {firstMember.SeriesBookId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.PreviousJobMissing:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "narration_jobs",
                    $"DELETE FROM narration_jobs WHERE \"Id\" = {firstPreviousJobId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.PreviousJobWrongBook:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "narration_jobs",
                    $"UPDATE narration_jobs SET \"BookId\" = {graph.Members[1].BookId} WHERE \"Id\" = {firstPreviousJobId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.PreviousJobNotPublished:
                await ExecuteSqlAsync(
                    graph.ConnectionString,
                    $"UPDATE narration_jobs SET \"Visibility\" = 'Historical' WHERE \"Id\" = {firstPreviousJobId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.PreviousJobIncomplete:
                await ExecuteSqlAsync(
                    graph.ConnectionString,
                    $"UPDATE narration_jobs SET \"Status\" = 'Running' WHERE \"Id\" = {firstPreviousJobId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.PreviousJobUnsafe:
                await ExecuteSqlAsync(
                    graph.ConnectionString,
                    $"UPDATE narration_jobs SET \"AudioRelativePath\" = '../synthetic-previous.mp3' WHERE \"Id\" = {firstPreviousJobId}",
                    cancellationToken);
                return;
            case PublisherValidationMutation.InconsistentEpochChain:
                await ExecuteWithTriggersDisabledAsync(
                    graph.ConnectionString,
                    "narration_cast_revisions",
                    $"""
                    UPDATE narration_cast_revisions
                    SET "EpochNumber" = 2
                    WHERE "Id" = (
                        SELECT "BaseActiveCastRevisionId"
                        FROM series_cast_rebuild_batches
                        WHERE "Id" = {graph.BatchId})
                    """,
                    cancellationToken);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static async Task CleanupValidationMutationAsync(
        PublisherValidationMutation mutation,
        ReadyGraph graph,
        CancellationToken cancellationToken)
    {
        if (mutation != PublisherValidationMutation.JobZeroBytes)
        {
            return;
        }

        await using var db = CreateContext(graph.ConnectionString);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE narration_jobs SET \"AudioBytes\" = 202 WHERE \"Id\" = {graph.JobIds[0]}",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE narration_jobs VALIDATE CONSTRAINT \"CK_narration_jobs_audio_bytes\"",
            cancellationToken);
    }

    private static async Task AddSyntheticSeriesBookAsync(
        ReadyGraph graph,
        CancellationToken cancellationToken)
    {
        var book = Book.Create(
            graph.OwnerId,
            $"synthetic new cohort book {graph.BatchId:N}",
            "Synthetic author",
            "en",
            $"synthetic-new-{graph.BatchId:N}.txt");
        var seriesBook = CreateSeriesBook(
            graph.OwnerId,
            graph.SeriesId,
            book.Id,
            "Synthetic new volume",
            1000,
            graph.Members.Length + 1);

        await using var db = CreateContext(graph.ConnectionString);
        db.Books.Add(book);
        db.SeriesBooks.Add(seriesBook);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Task AddSyntheticExtraMemberAsync(
        ReadyGraph graph,
        CancellationToken cancellationToken) =>
        ExecuteWithTriggersDisabledAsync(
            graph.ConnectionString,
            "series_cast_rebuild_members",
            $"""
            INSERT INTO series_cast_rebuild_members
                ("Id", "OwnerId", "SeriesId", "BatchId", "SeriesBookId", "BookId",
                 "MembershipRevision", "StagedNarrationJobId", "PreviousActiveNarrationJobId", "Status")
            VALUES
                ({Guid.NewGuid()}, {graph.OwnerId}, {graph.SeriesId}, {graph.BatchId}, {Guid.NewGuid()},
                 {Guid.NewGuid()}, 1, {Guid.NewGuid()}, NULL, 'Ready')
            """,
            cancellationToken);

    private static Task CorruptJobAsync(
        ReadyGraph graph,
        FormattableString assignment,
        CancellationToken cancellationToken)
    {
        var commandText = $"UPDATE narration_jobs SET {assignment.Format} WHERE \"Id\" = {{{assignment.ArgumentCount}}}";
        var arguments = assignment.GetArguments().Append(graph.JobIds[0]).ToArray();
        return ExecuteWithTriggersDisabledAsync(
            graph.ConnectionString,
            "narration_jobs",
            FormattableStringFactory.Create(commandText, arguments),
            cancellationToken);
    }

    private static Task SetJobPathAsync(
        ReadyGraph graph,
        string path,
        CancellationToken cancellationToken) =>
        ExecuteSqlAsync(
            graph.ConnectionString,
            $"UPDATE narration_jobs SET \"AudioRelativePath\" = {path} WHERE \"Id\" = {graph.JobIds[0]}",
            cancellationToken);

    private static async Task CorruptAudioBytesToZeroAsync(
        ReadyGraph graph,
        CancellationToken cancellationToken)
    {
        await using var db = CreateContext(graph.ConnectionString);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE narration_jobs DISABLE TRIGGER ALL",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE narration_jobs DROP CONSTRAINT \"CK_narration_jobs_audio_bytes\"",
            cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE narration_jobs SET \"AudioBytes\" = 0 WHERE \"Id\" = {graph.JobIds[0]}",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE narration_jobs
            ADD CONSTRAINT "CK_narration_jobs_audio_bytes"
            CHECK ("AudioBytes" IS NULL OR "AudioBytes" > 0) NOT VALID
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE narration_jobs ENABLE TRIGGER ALL",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteSqlAsync(
        string connectionString,
        FormattableString sql,
        CancellationToken cancellationToken)
    {
        await using var db = CreateContext(connectionString);
        await db.Database.ExecuteSqlInterpolatedAsync(sql, cancellationToken);
    }

    private static async Task ExecuteWithTriggersDisabledAsync(
        string connectionString,
        string tableName,
        FormattableString mutation,
        CancellationToken cancellationToken)
    {
        var triggerCommands = tableName switch
        {
            "story_series" => (
                Disable: "ALTER TABLE \"story_series\" DISABLE TRIGGER ALL",
                Enable: "ALTER TABLE \"story_series\" ENABLE TRIGGER ALL"),
            "series_books" => (
                Disable: "ALTER TABLE \"series_books\" DISABLE TRIGGER ALL",
                Enable: "ALTER TABLE \"series_books\" ENABLE TRIGGER ALL"),
            "narration_cast_revisions" => (
                Disable: "ALTER TABLE \"narration_cast_revisions\" DISABLE TRIGGER ALL",
                Enable: "ALTER TABLE \"narration_cast_revisions\" ENABLE TRIGGER ALL"),
            "series_cast_rebuild_batches" => (
                Disable: "ALTER TABLE \"series_cast_rebuild_batches\" DISABLE TRIGGER ALL",
                Enable: "ALTER TABLE \"series_cast_rebuild_batches\" ENABLE TRIGGER ALL"),
            "series_cast_rebuild_members" => (
                Disable: "ALTER TABLE \"series_cast_rebuild_members\" DISABLE TRIGGER ALL",
                Enable: "ALTER TABLE \"series_cast_rebuild_members\" ENABLE TRIGGER ALL"),
            "narration_jobs" => (
                Disable: "ALTER TABLE \"narration_jobs\" DISABLE TRIGGER ALL",
                Enable: "ALTER TABLE \"narration_jobs\" ENABLE TRIGGER ALL"),
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, null)
        };

        await using var db = CreateContext(connectionString);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            triggerCommands.Disable,
            cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(mutation, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            triggerCommands.Enable,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string> CapturePublisherStateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT jsonb_build_object(
                'series', COALESCE((
                    SELECT jsonb_agg(to_jsonb(snapshot_row) ORDER BY snapshot_row."Id")
                    FROM story_series AS snapshot_row), '[]'::jsonb),
                'series_books', COALESCE((
                    SELECT jsonb_agg(to_jsonb(snapshot_row) ORDER BY snapshot_row."Id")
                    FROM series_books AS snapshot_row), '[]'::jsonb),
                'revisions', COALESCE((
                    SELECT jsonb_agg(to_jsonb(snapshot_row) ORDER BY snapshot_row."Id")
                    FROM narration_cast_revisions AS snapshot_row), '[]'::jsonb),
                'batches', COALESCE((
                    SELECT jsonb_agg(to_jsonb(snapshot_row) ORDER BY snapshot_row."Id")
                    FROM series_cast_rebuild_batches AS snapshot_row), '[]'::jsonb),
                'members', COALESCE((
                    SELECT jsonb_agg(to_jsonb(snapshot_row) ORDER BY snapshot_row."Id")
                    FROM series_cast_rebuild_members AS snapshot_row), '[]'::jsonb),
                'jobs', COALESCE((
                    SELECT jsonb_agg(to_jsonb(snapshot_row) ORDER BY snapshot_row."Id")
                    FROM narration_jobs AS snapshot_row), '[]'::jsonb)
            )::text
            """,
            connection);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<ReadyGraph> CreateReadyGraphAsync(
        string connectionString,
        string prefix,
        bool useLegacyPredecessors,
        string migration,
        CancellationToken cancellationToken)
    {
        var ownerId = Guid.NewGuid();
        var books = Enumerable.Range(1, 2)
            .Select(index => Book.Create(
                ownerId,
                $"{prefix} synthetic book {index}",
                "Synthetic author",
                "en",
                $"{prefix}-{index}.txt"))
            .ToArray();
        var previousJobs = useLegacyPredecessors
            ? books.Select((book, index) => NarrationJob.Create(
                    ownerId,
                    book.Id,
                    book.Id,
                    $"{prefix}-legacy-{index}",
                    "synthetic-legacy-voice",
                    "+0%",
                    DateTimeOffset.UtcNow))
                .ToArray()
            : [];

        await using var db = CreateContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(migration, cancellationToken);
        db.Users.Add(CreateUser(ownerId, prefix));
        db.Books.AddRange(books);
        db.NarrationJobs.AddRange(previousJobs);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var previousJob in previousJobs)
        {
            var claimAt = DateTimeOffset.UtcNow;
            previousJob.Claim("synthetic-test-worker", claimAt.AddMinutes(1), claimAt);
            previousJob.Complete($"legacy/{prefix}/{previousJob.Id:N}.mp3", 101);
        }
        await db.SaveChangesAsync(cancellationToken);

        var series = StorySeries.Create(
            ownerId,
            $"{prefix} synthetic series",
            "synthetic-provider",
            "synthetic-narrator",
            "+0%",
            "+0Hz",
            "+0%",
            250);
        var memberships = books
            .Select((book, index) => series.AddBook(book, $"Volume {index + 1}", index + 1))
            .ToArray();
        for (var index = 0; index < memberships.Length; index++)
        {
            if (useLegacyPredecessors)
            {
                SwitchActiveJob(memberships[index], null, previousJobs[index].Id);
            }
        }

        var createdAt = DateTimeOffset.UtcNow;
        var revision = CreateRevision(ownerId, series.Id, revisionNumber: 1, prefix, createdAt);
        var batchId = Guid.NewGuid();
        var memberSnapshots = memberships
            .Select((membership, index) => SeriesCastRebuildMember.Create(
                Guid.NewGuid(),
                ownerId,
                series.Id,
                batchId,
                membership.Id,
                books[index].Id,
                membership.MembershipRevision,
                useLegacyPredecessors ? previousJobs[index].Id : null))
            .ToArray();
        var batch = SeriesCastRebuildBatch.Create(
            batchId,
            ownerId,
            series.Id,
            null,
            revision.Id,
            memberSnapshots.Max(member => member.MembershipRevision),
            createdAt,
            memberSnapshots);

        db.StorySeries.Add(series);
        db.NarrationCastRevisions.Add(revision);
        db.SeriesCastRebuildBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);

        var readyBatch = await PrepareBatchAsync(db, batch, prefix, cancellationToken);
        return new ReadyGraph(
            connectionString,
            ownerId,
            series.Id,
            revision.Id,
            batch.Id,
            readyBatch.Members,
            previousJobs.Select(job => job.Id).ToArray(),
            TruncateToPostgreSqlPrecision(DateTimeOffset.UtcNow));
    }

    private static async Task<ReadyBatch> AddReadyBatchAsync(
        ReadyGraph graph,
        Guid baseRevisionId,
        IReadOnlyList<Guid> previousJobIds,
        int revisionNumber,
        string prefix,
        CancellationToken cancellationToken)
    {
        await using var db = CreateContext(graph.ConnectionString);
        var seriesBooks = await db.SeriesBooks
            .Where(book => book.SeriesId == graph.SeriesId && book.OwnerId == graph.OwnerId)
            .OrderBy(book => book.Id)
            .ToArrayAsync(cancellationToken);
        var previousByBook = await db.NarrationJobs.AsNoTracking()
            .Where(job => previousJobIds.Contains(job.Id))
            .ToDictionaryAsync(job => job.BookId, job => job.Id, cancellationToken);
        var createdAt = DateTimeOffset.UtcNow;
        var revision = CreateRevision(graph.OwnerId, graph.SeriesId, revisionNumber, prefix, createdAt);
        var batchId = Guid.NewGuid();
        var members = seriesBooks
            .Select(seriesBook => SeriesCastRebuildMember.Create(
                Guid.NewGuid(),
                graph.OwnerId,
                graph.SeriesId,
                batchId,
                seriesBook.Id,
                seriesBook.BookId,
                seriesBook.MembershipRevision,
                previousByBook[seriesBook.BookId]))
            .ToArray();
        var batch = SeriesCastRebuildBatch.Create(
            batchId,
            graph.OwnerId,
            graph.SeriesId,
            baseRevisionId,
            revision.Id,
            members.Max(member => member.MembershipRevision),
            createdAt,
            members);
        db.NarrationCastRevisions.Add(revision);
        db.SeriesCastRebuildBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        var ready = await PrepareBatchAsync(db, batch, prefix, cancellationToken);
        return ready with { RevisionId = revision.Id };
    }

    private static async Task<ReadyBatch> PrepareBatchAsync(
        StoryVoiceDbContext db,
        SeriesCastRebuildBatch batch,
        string prefix,
        CancellationToken cancellationToken)
    {
        batch.StartBuilding(DateTimeOffset.UtcNow);
        var jobs = new List<NarrationJob>();
        foreach (var member in batch.Members)
        {
            var job = CreateMultiCharacterStagedJob(
                batch.OwnerId,
                member.BookId,
                batch.SeriesId,
                batch.DraftCastRevisionId,
                batch.Id,
                member.Id,
                $"{prefix}-staged-{member.Id:N}");
            batch.AttachStagedJob(member.SeriesBookId, job.Id);
            jobs.Add(job);
        }

        await using (var cycle = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            db.NarrationJobs.AddRange(jobs);
            await db.SaveChangesAsync(cancellationToken);
            await cycle.CommitAsync(cancellationToken);
        }

        foreach (var job in jobs)
        {
            var claimAt = DateTimeOffset.UtcNow;
            job.Claim("synthetic-test-worker", claimAt.AddMinutes(1), claimAt);
            job.Complete($"staged/{prefix}/{job.Id:N}.mp3", 202);
        }
        foreach (var member in batch.Members)
        {
            batch.MarkMemberReady(member.SeriesBookId, DateTimeOffset.UtcNow);
        }
        await db.SaveChangesAsync(cancellationToken);

        return new ReadyBatch(
            batch.Id,
            batch.DraftCastRevisionId,
            batch.Members.Select(member => new MemberFixture(
                member.SeriesBookId,
                member.BookId,
                member.Id,
                member.StagedNarrationJobId!.Value,
                member.PreviousActiveNarrationJobId)).ToArray());
    }

    private static NarrationCastRevision CreateRevision(
        Guid ownerId,
        Guid seriesId,
        int revisionNumber,
        string prefix,
        DateTimeOffset createdAt) =>
        NarrationCastRevision.Create(
            Guid.NewGuid(),
            ownerId,
            seriesId,
            revisionNumber,
            "synthetic-provider",
            "synthetic-provider-v1",
            $"synthetic-narrator-{prefix}",
            "+0%",
            "+0Hz",
            "+0%",
            250,
            500,
            $"composition-{prefix}",
            "mp3-128k",
            createdAt,
            []);

    private static NarrationJob CreateMultiCharacterStagedJob(
        Guid ownerId,
        Guid bookId,
        Guid seriesId,
        Guid castRevisionId,
        Guid batchId,
        Guid memberId,
        string sourceHash)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(typeof(NarrationJob).GetMethod(
            "CreateMultiCharacterStaged",
            BindingFlags.Static | BindingFlags.NonPublic));
        return Assert.IsType<NarrationJob>(method.Invoke(
            null,
            [
                ownerId,
                bookId,
                bookId,
                seriesId,
                castRevisionId,
                Guid.NewGuid(),
                batchId,
                memberId,
                sourceHash,
                DateTimeOffset.UtcNow
            ]));
    }

    private static void SwitchActiveJob(SeriesBook seriesBook, Guid? expected, Guid next)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(typeof(SeriesBook).GetMethod(
            "SwitchActiveNarrationJob",
            BindingFlags.Instance | BindingFlags.NonPublic));
        method.Invoke(seriesBook, [expected, next]);
    }

    private static SeriesBook CreateSeriesBook(
        Guid ownerId,
        Guid seriesId,
        Guid bookId,
        string volumeLabel,
        int sortOrder,
        int membershipRevision)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(typeof(SeriesBook).GetMethod(
            "Create",
            BindingFlags.Static | BindingFlags.NonPublic));
        return Assert.IsType<SeriesBook>(method.Invoke(
            null,
            [ownerId, seriesId, bookId, volumeLabel, sortOrder, membershipRevision]));
    }

    private static Task<CastEpochActivationResult> ActivateAsync(
        ReadyGraph graph,
        CancellationToken cancellationToken) =>
        ActivateAsync(
            graph.ConnectionString,
            graph.OwnerId,
            graph.SeriesId,
            graph.BatchId,
            graph.ActivatedAt,
            cancellationToken);

    private static async Task<CastEpochActivationResult> ActivateAsync(
        string connectionString,
        Guid ownerId,
        Guid seriesId,
        Guid batchId,
        DateTimeOffset activatedAt,
        CancellationToken cancellationToken)
    {
        await using var db = CreateContext(connectionString);
        var publisher = new PostgreSqlCastEpochActivationPublisher(db);
        return await publisher.ActivateAsync(
            new CastEpochActivationCommand(ownerId, seriesId, batchId, activatedAt),
            cancellationToken);
    }

    private static async Task<ActivationOutcome> RunActivationAsync(
        string connectionString,
        ReadyGraph graph,
        Guid batchId,
        DateTimeOffset activatedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ActivateAsync(
                connectionString,
                graph.OwnerId,
                graph.SeriesId,
                batchId,
                activatedAt,
                cancellationToken);
            return new ActivationOutcome(result, null);
        }
        catch (Exception exception)
        {
            return new ActivationOutcome(null, exception);
        }
    }

    private static async Task AssertCurrentEpochAsync(
        string connectionString,
        ReadyGraph graph,
        int expectedEpoch,
        NarrationCastRevisionStatus expectedRevisionStatus,
        NarrationArtifactVisibility expectedJobVisibility,
        CancellationToken cancellationToken)
    {
        await using var proof = CreateContext(connectionString);
        var series = await proof.StorySeries.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.SeriesId, cancellationToken);
        var revision = await proof.NarrationCastRevisions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.RevisionId, cancellationToken);
        var batch = await proof.SeriesCastRebuildBatches.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.BatchId, cancellationToken);
        var jobs = await proof.NarrationJobs.AsNoTracking()
            .Where(candidate => graph.JobIds.Contains(candidate.Id))
            .ToArrayAsync(cancellationToken);
        var pointers = await proof.SeriesBooks.AsNoTracking()
            .Where(candidate => candidate.SeriesId == graph.SeriesId)
            .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.ActiveNarrationJobId, cancellationToken);

        Assert.Equal(graph.RevisionId, series.ActiveCastRevisionId);
        Assert.Equal(expectedRevisionStatus, revision.Status);
        Assert.Equal(expectedEpoch, revision.EpochNumber);
        Assert.Equal(graph.ActivatedAt, revision.ActivatedAt);
        Assert.Equal(SeriesCastRebuildBatchStatus.Activated, batch.Status);
        Assert.Equal(graph.JobIds.Length, jobs.Length);
        Assert.All(jobs, job => Assert.Equal(expectedJobVisibility, job.Visibility));
        Assert.All(graph.Members, member => Assert.Equal(member.JobId, pointers[member.SeriesBookId]));
    }

    private static async Task AssertDeferredFailureAsync(
        string connectionString,
        Func<StoryVoiceDbContext, Task> mutation,
        string sqlState,
        string constraintName,
        Func<StoryVoiceDbContext, Task> assertRolledBack,
        CancellationToken cancellationToken)
    {
        await using (var db = CreateContext(connectionString))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await mutation(db);
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                transaction.CommitAsync(cancellationToken));
            Assert.Equal(sqlState, exception.SqlState);
            Assert.Equal(constraintName, exception.ConstraintName);
        }

        await using var proof = CreateContext(connectionString);
        await assertRolledBack(proof);
    }

    private static StoryVoiceDbContext CreateContext(
        string connectionString,
        SaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<StoryVoiceDbContext>()
            .UseNpgsql(connectionString);
        options.AddInterceptors(new HistoricalBooksSchemaCompatibilityInterceptor());
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }
        return new StoryVoiceDbContext(options.Options);
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync(CancellationToken cancellationToken)
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        try
        {
            await postgres.StartAsync(cancellationToken);
            return postgres;
        }
        catch
        {
            await postgres.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> CreateDatabaseAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres"
        };
        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName
        }.ConnectionString;
    }

    private static async Task<bool> TriggerExistsAsync(
        string connectionString,
        string triggerName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = @triggerName AND NOT tgisinternal)",
            connection);
        command.Parameters.AddWithValue("triggerName", triggerName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static ApplicationUser CreateUser(Guid id, string prefix) =>
        new()
        {
            Id = id,
            UserName = prefix,
            NormalizedUserName = prefix.ToUpperInvariant(),
            Email = $"{prefix}@example.invalid",
            NormalizedEmail = $"{prefix.ToUpperInvariant()}@EXAMPLE.INVALID",
            SecurityStamp = Guid.NewGuid().ToString("N")
        };

    private static DateTimeOffset TruncateToPostgreSqlPrecision(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    private sealed class SaveProbeInterceptor(int? throwOnSaveCall = null) : SaveChangesInterceptor
    {
        public int SaveCalls { get; private set; }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            OnSaving();
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            OnSaving();
            return ValueTask.FromResult(result);
        }

        private void OnSaving()
        {
            SaveCalls++;
            if (SaveCalls == throwOnSaveCall)
            {
                throw new InjectedStageTwoException();
            }
        }
    }

    private sealed class InjectedStageTwoException : Exception;

    private enum ValidationGraphKind
    {
        FirstActivation,
        LegacyPredecessors,
        Superseding
    }

    private enum PublisherValidationCommand
    {
        Default,
        WrongOwner,
        WrongSeries,
        MissingBatch,
        TimestampRegression
    }

    private enum PublisherValidationMutation
    {
        None,
        BatchNotReady,
        StaleBasePointer,
        DraftWrongId,
        DraftWrongScope,
        DraftWrongState,
        BaseWrongState,
        NewSeriesBook,
        MissingSeriesBook,
        StaleMembershipTuple,
        MissingMember,
        ExtraMember,
        MemberNotReady,
        NullStagedPointer,
        WrongStagedPointer,
        JobWrongOwner,
        JobWrongSeries,
        JobWrongBatch,
        JobWrongMember,
        JobWrongBook,
        JobWrongCast,
        JobNotCompleted,
        JobPublished,
        JobHistorical,
        JobZeroBytes,
        JobRootedPath,
        JobDriveLetterPath,
        JobDotSegmentPath,
        PreviousPointerChanged,
        PreviousJobMissing,
        PreviousJobWrongBook,
        PreviousJobNotPublished,
        PreviousJobIncomplete,
        PreviousJobUnsafe,
        InconsistentEpochChain
    }

    private sealed record PublisherValidationCase(
        string Name,
        ValidationGraphKind GraphKind,
        PublisherValidationMutation Mutation,
        PublisherValidationCommand Command,
        CastEpochActivationFailure ExpectedFailure);

    private sealed record MemberFixture(
        Guid SeriesBookId,
        Guid BookId,
        Guid MemberId,
        Guid JobId,
        Guid? PreviousJobId);

    private sealed record ReadyBatch(
        Guid BatchId,
        Guid RevisionId,
        MemberFixture[] Members)
    {
        public Guid[] JobIds => Members.Select(member => member.JobId).ToArray();
    }

    private sealed record ReadyGraph(
        string ConnectionString,
        Guid OwnerId,
        Guid SeriesId,
        Guid RevisionId,
        Guid BatchId,
        MemberFixture[] Members,
        Guid[] PreviousJobIds,
        DateTimeOffset ActivatedAt)
    {
        public Guid[] JobIds => Members.Select(member => member.JobId).ToArray();
    }

    private sealed record ActivationOutcome(
        CastEpochActivationResult? Result,
        Exception? Exception);
}

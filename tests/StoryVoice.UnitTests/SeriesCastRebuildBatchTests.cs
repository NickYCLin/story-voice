using System.Reflection;
using System.Runtime.ExceptionServices;
using StoryVoice.Domain.Narrations;

namespace StoryVoice.UnitTests;

public sealed class SeriesCastRebuildBatchTests
{
    [Fact]
    public void Resume_failed_batch_preserves_completed_jobs_and_all_original_bindings()
    {
        var fixture = CreateFixture();
        var batch = fixture.Batch.Create();
        var firstJob = Guid.NewGuid();
        var secondJob = Guid.NewGuid();
        batch.StartBuilding(DateTimeOffset.UtcNow);
        batch.AttachStagedJob(fixture.First.SeriesBookId, firstJob);
        batch.AttachStagedJob(fixture.Second.SeriesBookId, secondJob);
        batch.MarkMemberReady(fixture.First.SeriesBookId, DateTimeOffset.UtcNow);
        batch.MarkMemberFailed(fixture.Second.SeriesBookId, DateTimeOffset.UtcNow);
        batch.ResumeFailed(new HashSet<Guid> { firstJob }, DateTimeOffset.UtcNow);
        Assert.Equal(SeriesCastRebuildBatchStatus.Building, batch.Status);
        Assert.Equal(SeriesCastRebuildMemberStatus.Ready, batch.Members[0].Status);
        Assert.Equal(SeriesCastRebuildMemberStatus.Building, batch.Members[1].Status);
        Assert.Equal(firstJob, batch.Members[0].StagedNarrationJobId);
        Assert.Equal(secondJob, batch.Members[1].StagedNarrationJobId);
        Assert.Equal(fixture.Batch.DraftCastRevisionId, batch.DraftCastRevisionId);
        batch.MarkMemberReady(fixture.Second.SeriesBookId, DateTimeOffset.UtcNow);
        Assert.Equal(SeriesCastRebuildBatchStatus.ReadyToActivate, batch.Status);
        Assert.Throws<InvalidOperationException>(() => batch.ResumeFailed(new HashSet<Guid>(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Creation_captures_an_immutable_pending_cohort_and_exact_status_enums()
    {
        var fixture = CreateFixture();
        var source = fixture.Batch.Members.ToList();

        var batch = (fixture.Batch with { Members = source }).Create();

        Assert.Equal(fixture.Batch.Id, batch.Id);
        Assert.Equal(fixture.Batch.OwnerId, batch.OwnerId);
        Assert.Equal(fixture.Batch.SeriesId, batch.SeriesId);
        Assert.Equal(fixture.Batch.BaseActiveCastRevisionId, batch.BaseActiveCastRevisionId);
        Assert.Equal(fixture.Batch.DraftCastRevisionId, batch.DraftCastRevisionId);
        Assert.Equal(7, batch.CohortMembershipRevision);
        Assert.Equal(SeriesCastRebuildBatchStatus.Draft, batch.Status);
        Assert.Equal(fixture.Batch.CreatedAt, batch.CreatedAt);
        Assert.Equal(fixture.Batch.CreatedAt, batch.UpdatedAt);
        Assert.Equal(2, batch.Members.Count);
        Assert.Collection(
            batch.Members,
            member => AssertMemberSnapshot(fixture.First, member),
            member => AssertMemberSnapshot(fixture.Second, member));
        Assert.Equal(
            [
                SeriesCastRebuildBatchStatus.Draft,
                SeriesCastRebuildBatchStatus.Building,
                SeriesCastRebuildBatchStatus.ReadyToActivate,
                SeriesCastRebuildBatchStatus.Activated,
                SeriesCastRebuildBatchStatus.Failed
            ],
            Enum.GetValues<SeriesCastRebuildBatchStatus>());
        Assert.Equal(
            [
                SeriesCastRebuildMemberStatus.Pending,
                SeriesCastRebuildMemberStatus.Building,
                SeriesCastRebuildMemberStatus.Ready,
                SeriesCastRebuildMemberStatus.Failed
            ],
            Enum.GetValues<SeriesCastRebuildMemberStatus>());

        source.Clear();

        Assert.Equal(2, batch.Members.Count);
    }

    [Fact]
    public void Duplicate_member_series_book_and_book_ids_are_each_rejected()
    {
        var fixture = CreateFixture();

        Assert.Throws<InvalidOperationException>(() => (fixture.Batch with
        {
            Members = [fixture.First.Create(), (fixture.Second with { Id = fixture.First.Id }).Create()]
        }).Create());
        Assert.Throws<InvalidOperationException>(() => (fixture.Batch with
        {
            Members =
            [
                fixture.First.Create(),
                (fixture.Second with { SeriesBookId = fixture.First.SeriesBookId }).Create()
            ]
        }).Create());
        Assert.Throws<InvalidOperationException>(() => (fixture.Batch with
        {
            Members = [fixture.First.Create(), (fixture.Second with { BookId = fixture.First.BookId }).Create()]
        }).Create());
    }

    [Fact]
    public void Cross_owner_series_and_batch_members_are_each_rejected()
    {
        var fixture = CreateFixture();

        Assert.Throws<InvalidOperationException>(() => (fixture.Batch with
        {
            Members = [(fixture.First with { OwnerId = Guid.NewGuid() }).Create()]
        }).Create());
        Assert.Throws<InvalidOperationException>(() => (fixture.Batch with
        {
            Members = [(fixture.First with { SeriesId = Guid.NewGuid() }).Create()]
        }).Create());
        Assert.Throws<InvalidOperationException>(() => (fixture.Batch with
        {
            Members = [(fixture.First with { BatchId = Guid.NewGuid() }).Create()]
        }).Create());
    }

    [Fact]
    public void Empty_ids_invalid_nullable_ids_and_non_positive_revisions_are_rejected()
    {
        var fixture = CreateFixture();
        var batchInputs = new BatchInput[]
        {
            fixture.Batch with { Id = Guid.Empty },
            fixture.Batch with { OwnerId = Guid.Empty },
            fixture.Batch with { SeriesId = Guid.Empty },
            fixture.Batch with { BaseActiveCastRevisionId = Guid.Empty },
            fixture.Batch with { DraftCastRevisionId = Guid.Empty },
            fixture.Batch with { CohortMembershipRevision = 0 },
            fixture.Batch with { CohortMembershipRevision = -1 }
        };
        var memberInputs = new MemberInput[]
        {
            fixture.First with { Id = Guid.Empty },
            fixture.First with { OwnerId = Guid.Empty },
            fixture.First with { SeriesId = Guid.Empty },
            fixture.First with { BatchId = Guid.Empty },
            fixture.First with { SeriesBookId = Guid.Empty },
            fixture.First with { BookId = Guid.Empty },
            fixture.First with { MembershipRevision = 0 },
            fixture.First with { MembershipRevision = -1 },
            fixture.First with { PreviousActiveNarrationJobId = Guid.Empty }
        };

        Assert.All(batchInputs, input => Assert.ThrowsAny<ArgumentException>(input.Create));
        Assert.All(memberInputs, input => Assert.ThrowsAny<ArgumentException>(input.Create));
        Assert.Null((fixture.Batch with { BaseActiveCastRevisionId = null }).Create().BaseActiveCastRevisionId);
        Assert.Null((fixture.Second with { PreviousActiveNarrationJobId = null }).Create()
            .PreviousActiveNarrationJobId);
    }

    [Fact]
    public void Creation_requires_members_a_matching_positive_cohort_max_and_distinct_base_and_draft_revisions()
    {
        var fixture = CreateFixture();

        Assert.Throws<ArgumentNullException>(() => (fixture.Batch with { Members = null! }).Create());
        Assert.Throws<ArgumentException>(() => (fixture.Batch with
        {
            Members = Array.Empty<SeriesCastRebuildMember>()
        }).Create());
        Assert.Throws<ArgumentException>(() => (fixture.Batch with
        {
            Members = [null!]
        }).Create());
        Assert.Throws<InvalidOperationException>(() => (fixture.Batch with
        {
            CohortMembershipRevision = 6
        }).Create());
        Assert.Throws<InvalidOperationException>(() => (fixture.Batch with
        {
            CohortMembershipRevision = 8
        }).Create());
        Assert.Throws<ArgumentException>(() => (fixture.Batch with
        {
            BaseActiveCastRevisionId = fixture.Batch.DraftCastRevisionId
        }).Create());
    }

    [Fact]
    public void Legal_multi_member_path_becomes_ready_only_after_every_member_then_activates()
    {
        var fixture = CreateFixture();
        var batch = fixture.Batch.Create();
        var startAt = batch.CreatedAt.AddMinutes(1);
        var firstJobId = Guid.NewGuid();
        var secondJobId = Guid.NewGuid();

        batch.StartBuilding(startAt);

        Assert.Equal(SeriesCastRebuildBatchStatus.Building, batch.Status);
        Assert.Equal(startAt, batch.UpdatedAt);

        var beforeFirstAttachCall = DateTimeOffset.UtcNow;
        batch.AttachStagedJob(fixture.First.SeriesBookId, firstJobId);
        var afterFirstAttachCall = DateTimeOffset.UtcNow;
        var firstAttachUpdatedAt = batch.UpdatedAt;
        Assert.InRange(firstAttachUpdatedAt, beforeFirstAttachCall, afterFirstAttachCall);
        Assert.NotEqual(startAt, firstAttachUpdatedAt);

        var beforeSecondAttachCall = DateTimeOffset.UtcNow;
        batch.AttachStagedJob(fixture.Second.SeriesBookId, secondJobId);
        var afterSecondAttachCall = DateTimeOffset.UtcNow;
        var secondAttachUpdatedAt = batch.UpdatedAt;
        Assert.InRange(secondAttachUpdatedAt, beforeSecondAttachCall, afterSecondAttachCall);

        Assert.Equal(SeriesCastRebuildBatchStatus.Building, batch.Status);
        Assert.Equal(SeriesCastRebuildMemberStatus.Building, batch.Members[0].Status);
        Assert.Equal(firstJobId, batch.Members[0].StagedNarrationJobId);
        Assert.Equal(SeriesCastRebuildMemberStatus.Building, batch.Members[1].Status);
        Assert.Equal(secondJobId, batch.Members[1].StagedNarrationJobId);

        var firstReadyAt = batch.UpdatedAt.AddMinutes(1);
        batch.MarkMemberReady(fixture.First.SeriesBookId, firstReadyAt);

        Assert.Equal(SeriesCastRebuildBatchStatus.Building, batch.Status);
        Assert.Equal(SeriesCastRebuildMemberStatus.Ready, batch.Members[0].Status);
        Assert.Equal(SeriesCastRebuildMemberStatus.Building, batch.Members[1].Status);
        Assert.Equal(firstReadyAt, batch.UpdatedAt);

        var allReadyAt = firstReadyAt.AddMinutes(1);
        batch.MarkMemberReady(fixture.Second.SeriesBookId, allReadyAt);

        Assert.Equal(SeriesCastRebuildBatchStatus.ReadyToActivate, batch.Status);
        Assert.All(batch.Members, member => Assert.Equal(SeriesCastRebuildMemberStatus.Ready, member.Status));
        Assert.Equal(allReadyAt, batch.UpdatedAt);

        var activatedAt = allReadyAt.AddMinutes(1);
        InvokeInternal(batch, "MarkActivated", activatedAt);

        Assert.Equal(SeriesCastRebuildBatchStatus.Activated, batch.Status);
        Assert.Equal(activatedAt, batch.UpdatedAt);
        Assert.Equal(firstJobId, batch.Members[0].StagedNarrationJobId);
        Assert.Equal(secondJobId, batch.Members[1].StagedNarrationJobId);
    }

    [Fact]
    public void Member_failure_moves_the_batch_to_failed_without_rewriting_other_member_snapshots()
    {
        var fixture = CreateFixture();
        var batch = fixture.Batch.Create();
        batch.StartBuilding(batch.CreatedAt.AddMinutes(1));
        var firstJobId = Guid.NewGuid();
        batch.AttachStagedJob(fixture.First.SeriesBookId, firstJobId);
        var failedAt = batch.UpdatedAt.AddMinutes(1);

        batch.MarkMemberFailed(fixture.First.SeriesBookId, failedAt);

        Assert.Equal(SeriesCastRebuildBatchStatus.Failed, batch.Status);
        Assert.Equal(failedAt, batch.UpdatedAt);
        Assert.Equal(SeriesCastRebuildMemberStatus.Failed, batch.Members[0].Status);
        Assert.Equal(firstJobId, batch.Members[0].StagedNarrationJobId);
        Assert.Equal(SeriesCastRebuildMemberStatus.Pending, batch.Members[1].Status);
        Assert.Null(batch.Members[1].StagedNarrationJobId);
    }

    [Fact]
    public void Pending_member_can_fail_without_a_staged_job()
    {
        var fixture = CreateFixture();
        var batch = fixture.Batch.Create();
        batch.StartBuilding(batch.CreatedAt);

        batch.MarkMemberFailed(fixture.Second.SeriesBookId, batch.UpdatedAt);

        Assert.Equal(SeriesCastRebuildBatchStatus.Failed, batch.Status);
        Assert.Equal(SeriesCastRebuildMemberStatus.Failed, batch.Members[1].Status);
        Assert.Null(batch.Members[1].StagedNarrationJobId);
        Assert.Equal(batch.CreatedAt, batch.UpdatedAt);
    }

    [Fact]
    public void Invalidate_is_legal_from_each_pre_active_state_and_preserves_member_audit_snapshots()
    {
        var fixture = CreateFixture();

        var draft = fixture.Batch.Create();
        AssertAtomicFailure<InvalidOperationException>(
            draft,
            () => InvokeInternal(draft, "MarkActivated", draft.CreatedAt));
        InvokeInternal(draft, "Invalidate", draft.CreatedAt.AddMinutes(1));
        Assert.Equal(SeriesCastRebuildBatchStatus.Failed, draft.Status);
        Assert.All(draft.Members, member => Assert.Equal(SeriesCastRebuildMemberStatus.Pending, member.Status));

        var building = fixture.Batch.Create();
        building.StartBuilding(building.CreatedAt.AddMinutes(1));
        var stagedJobId = Guid.NewGuid();
        building.AttachStagedJob(fixture.First.SeriesBookId, stagedJobId);
        var buildingInvalidatedAt = building.UpdatedAt.AddMinutes(1);
        InvokeInternal(building, "Invalidate", buildingInvalidatedAt);
        Assert.Equal(SeriesCastRebuildBatchStatus.Failed, building.Status);
        Assert.Equal(SeriesCastRebuildMemberStatus.Building, building.Members[0].Status);
        Assert.Equal(stagedJobId, building.Members[0].StagedNarrationJobId);

        var ready = CreateReadyBatch(fixture);
        var readySnapshot = Capture(ready);
        var readyInvalidatedAt = ready.UpdatedAt.AddMinutes(1);
        InvokeInternal(ready, "Invalidate", readyInvalidatedAt);
        Assert.Equal(SeriesCastRebuildBatchStatus.Failed, ready.Status);
        Assert.Equal(readyInvalidatedAt, ready.UpdatedAt);
        Assert.Equal(readySnapshot.Members, Capture(ready).Members);
    }

    [Fact]
    public void Activated_and_failed_batches_are_terminal_without_silent_idempotence()
    {
        var fixture = CreateFixture();
        var activated = CreateReadyBatch(fixture);
        InvokeInternal(activated, "MarkActivated", activated.UpdatedAt.AddMinutes(1));

        AssertAtomicFailure<InvalidOperationException>(activated, () => activated.StartBuilding(activated.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            activated,
            () => activated.AttachStagedJob(fixture.First.SeriesBookId, Guid.NewGuid()));
        AssertAtomicFailure<InvalidOperationException>(
            activated,
            () => activated.MarkMemberReady(fixture.First.SeriesBookId, activated.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            activated,
            () => activated.MarkMemberFailed(fixture.First.SeriesBookId, activated.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            activated,
            () => InvokeInternal(activated, "MarkActivated", activated.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            activated,
            () => InvokeInternal(activated, "Invalidate", activated.UpdatedAt.AddMinutes(1)));

        var failed = fixture.Batch.Create();
        InvokeInternal(failed, "Invalidate", failed.CreatedAt.AddMinutes(1));

        AssertAtomicFailure<InvalidOperationException>(failed, () => failed.StartBuilding(failed.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            failed,
            () => failed.AttachStagedJob(fixture.First.SeriesBookId, Guid.NewGuid()));
        AssertAtomicFailure<InvalidOperationException>(
            failed,
            () => failed.MarkMemberReady(fixture.First.SeriesBookId, failed.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            failed,
            () => failed.MarkMemberFailed(fixture.First.SeriesBookId, failed.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            failed,
            () => InvokeInternal(failed, "MarkActivated", failed.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            failed,
            () => InvokeInternal(failed, "Invalidate", failed.UpdatedAt.AddMinutes(1)));
    }

    [Fact]
    public void Non_terminal_state_rejection_matrix_is_atomic()
    {
        var fixture = CreateFixture();
        var ready = CreateReadyBatch(fixture);

        AssertAtomicFailure<InvalidOperationException>(ready, () => ready.StartBuilding(ready.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            ready,
            () => ready.AttachStagedJob(fixture.First.SeriesBookId, Guid.NewGuid()));
        AssertAtomicFailure<InvalidOperationException>(
            ready,
            () => ready.MarkMemberReady(fixture.First.SeriesBookId, ready.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            ready,
            () => ready.MarkMemberFailed(fixture.First.SeriesBookId, ready.UpdatedAt.AddMinutes(1)));

        var building = fixture.Batch.Create();
        building.StartBuilding(building.CreatedAt.AddMinutes(1));
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => InvokeInternal(building, "MarkActivated", building.UpdatedAt.AddMinutes(1)));
    }

    [Fact]
    public void Attach_failures_leave_the_entire_batch_and_member_cohort_unchanged()
    {
        var fixture = CreateFixture();
        var draft = fixture.Batch.Create();
        AssertAtomicFailure<InvalidOperationException>(
            draft,
            () => draft.AttachStagedJob(fixture.First.SeriesBookId, Guid.NewGuid()));

        var building = fixture.Batch.Create();
        building.StartBuilding(building.CreatedAt.AddMinutes(1));
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.AttachStagedJob(Guid.NewGuid(), Guid.NewGuid()));
        AssertAtomicFailure<ArgumentException>(
            building,
            () => building.AttachStagedJob(fixture.First.SeriesBookId, Guid.Empty));
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.AttachStagedJob(
                fixture.First.SeriesBookId,
                fixture.First.PreviousActiveNarrationJobId!.Value));

        var firstJobId = Guid.NewGuid();
        building.AttachStagedJob(fixture.First.SeriesBookId, firstJobId);
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.AttachStagedJob(fixture.First.SeriesBookId, Guid.NewGuid()));
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.AttachStagedJob(fixture.Second.SeriesBookId, firstJobId));
    }

    [Fact]
    public void Ready_and_fail_rejections_are_atomic_for_wrong_state_unknown_member_and_illegal_member_state()
    {
        var fixture = CreateFixture();
        var draft = fixture.Batch.Create();
        AssertAtomicFailure<InvalidOperationException>(
            draft,
            () => draft.MarkMemberReady(fixture.First.SeriesBookId, draft.CreatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            draft,
            () => draft.MarkMemberFailed(fixture.First.SeriesBookId, draft.CreatedAt.AddMinutes(1)));

        var building = fixture.Batch.Create();
        building.StartBuilding(building.CreatedAt.AddMinutes(1));
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.StartBuilding(building.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.MarkMemberReady(Guid.NewGuid(), building.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.MarkMemberReady(fixture.First.SeriesBookId, building.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.MarkMemberFailed(Guid.NewGuid(), building.UpdatedAt.AddMinutes(1)));

        building.AttachStagedJob(fixture.First.SeriesBookId, Guid.NewGuid());
        building.MarkMemberReady(fixture.First.SeriesBookId, building.UpdatedAt.AddMinutes(1));
        Assert.Equal(SeriesCastRebuildBatchStatus.Building, building.Status);
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.MarkMemberReady(fixture.First.SeriesBookId, building.UpdatedAt.AddMinutes(1)));
        AssertAtomicFailure<InvalidOperationException>(
            building,
            () => building.MarkMemberFailed(fixture.First.SeriesBookId, building.UpdatedAt.AddMinutes(1)));
    }

    [Fact]
    public void Time_regression_is_rejected_atomically_and_exact_current_time_is_accepted()
    {
        var fixture = CreateFixture();
        var draft = fixture.Batch.Create();
        AssertAtomicFailure<ArgumentOutOfRangeException>(
            draft,
            () => draft.StartBuilding(draft.CreatedAt.AddTicks(-1)));

        draft.StartBuilding(draft.CreatedAt);
        Assert.Equal(draft.CreatedAt, draft.UpdatedAt);
        draft.AttachStagedJob(fixture.First.SeriesBookId, Guid.NewGuid());
        AssertAtomicFailure<ArgumentOutOfRangeException>(
            draft,
            () => draft.MarkMemberReady(fixture.First.SeriesBookId, draft.UpdatedAt.AddTicks(-1)));
        AssertAtomicFailure<ArgumentOutOfRangeException>(
            draft,
            () => draft.MarkMemberFailed(fixture.Second.SeriesBookId, draft.UpdatedAt.AddTicks(-1)));
        AssertAtomicFailure<ArgumentOutOfRangeException>(
            draft,
            () => InvokeInternal(draft, "Invalidate", draft.UpdatedAt.AddTicks(-1)));

        var ready = CreateReadyBatch(fixture);
        AssertAtomicFailure<ArgumentOutOfRangeException>(
            ready,
            () => InvokeInternal(ready, "MarkActivated", ready.UpdatedAt.AddTicks(-1)));
        InvokeInternal(ready, "MarkActivated", ready.UpdatedAt);
        Assert.Equal(SeriesCastRebuildBatchStatus.Activated, ready.Status);
    }

    [Fact]
    public void Public_and_internal_surface_keeps_members_readonly_and_EF_field_access_compatible()
    {
        var fixture = CreateFixture();
        var source = fixture.Batch.Members.ToList();
        var batch = (fixture.Batch with { Members = source }).Create();

        var membersField = typeof(SeriesCastRebuildBatch).GetField(
            "_members",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(membersField);
        Assert.Equal(typeof(List<SeriesCastRebuildMember>), membersField.FieldType);
        Assert.True(membersField.IsInitOnly);
        Assert.Equal(typeof(IReadOnlyList<SeriesCastRebuildMember>),
            typeof(SeriesCastRebuildBatch).GetProperty(nameof(SeriesCastRebuildBatch.Members))!.PropertyType);
        Assert.Null(typeof(SeriesCastRebuildBatch).GetProperty(nameof(SeriesCastRebuildBatch.Members))!
            .GetSetMethod(nonPublic: true));
        Assert.NotSame(source, batch.Members);
        Assert.NotSame(source[0], batch.Members[0]);

        batch.StartBuilding(batch.CreatedAt);
        batch.AttachStagedJob(fixture.First.SeriesBookId, Guid.NewGuid());
        Assert.Equal(SeriesCastRebuildMemberStatus.Pending, source[0].Status);
        Assert.Null(source[0].StagedNarrationJobId);

        var mutableProjection = Assert.IsAssignableFrom<ICollection<SeriesCastRebuildMember>>(batch.Members);
        Assert.True(mutableProjection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableProjection.Add(fixture.First.Create()));
        Assert.Throws<NotSupportedException>(() => mutableProjection.Remove(batch.Members[0]));
        Assert.Throws<NotSupportedException>(mutableProjection.Clear);
        Assert.Equal(2, batch.Members.Count);

        AssertInternalMethod("MarkActivated");
        AssertInternalMethod("Invalidate");
        Assert.True(typeof(SeriesCastRebuildBatch).GetMethod(nameof(SeriesCastRebuildBatch.StartBuilding))!.IsPublic);
        var attachMethod = typeof(SeriesCastRebuildBatch).GetMethod(nameof(SeriesCastRebuildBatch.AttachStagedJob));
        Assert.NotNull(attachMethod);
        Assert.True(attachMethod.IsPublic);
        Assert.Equal([typeof(Guid), typeof(Guid)], attachMethod.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.True(typeof(SeriesCastRebuildBatch).GetMethod(nameof(SeriesCastRebuildBatch.MarkMemberReady))!.IsPublic);
        Assert.True(typeof(SeriesCastRebuildBatch).GetMethod(nameof(SeriesCastRebuildBatch.MarkMemberFailed))!.IsPublic);

        Assert.All(
            typeof(SeriesCastRebuildMember).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.Null(property.GetSetMethod(nonPublic: true)));
        Assert.DoesNotContain(
            typeof(SeriesCastRebuildMember).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => !method.IsSpecialName);

        var immutableBatchProperties = new[]
        {
            nameof(SeriesCastRebuildBatch.Id),
            nameof(SeriesCastRebuildBatch.OwnerId),
            nameof(SeriesCastRebuildBatch.SeriesId),
            nameof(SeriesCastRebuildBatch.BaseActiveCastRevisionId),
            nameof(SeriesCastRebuildBatch.DraftCastRevisionId),
            nameof(SeriesCastRebuildBatch.CohortMembershipRevision),
            nameof(SeriesCastRebuildBatch.CreatedAt)
        };
        Assert.All(
            immutableBatchProperties,
            propertyName => Assert.Null(typeof(SeriesCastRebuildBatch).GetProperty(propertyName)!
                .GetSetMethod(nonPublic: true)));
        var immutableMemberProperties = new[]
        {
            nameof(SeriesCastRebuildMember.Id),
            nameof(SeriesCastRebuildMember.OwnerId),
            nameof(SeriesCastRebuildMember.SeriesId),
            nameof(SeriesCastRebuildMember.BatchId),
            nameof(SeriesCastRebuildMember.SeriesBookId),
            nameof(SeriesCastRebuildMember.BookId),
            nameof(SeriesCastRebuildMember.MembershipRevision),
            nameof(SeriesCastRebuildMember.PreviousActiveNarrationJobId)
        };
        Assert.All(
            immutableMemberProperties,
            propertyName => Assert.Null(typeof(SeriesCastRebuildMember).GetProperty(propertyName)!
                .GetSetMethod(nonPublic: true)));
    }

    private static SeriesCastRebuildBatch CreateReadyBatch(CohortFixture fixture)
    {
        var batch = fixture.Batch.Create();
        batch.StartBuilding(batch.CreatedAt.AddMinutes(1));
        batch.AttachStagedJob(fixture.First.SeriesBookId, Guid.NewGuid());
        batch.AttachStagedJob(fixture.Second.SeriesBookId, Guid.NewGuid());
        batch.MarkMemberReady(fixture.First.SeriesBookId, batch.UpdatedAt.AddMinutes(1));
        batch.MarkMemberReady(fixture.Second.SeriesBookId, batch.UpdatedAt.AddMinutes(1));
        Assert.Equal(SeriesCastRebuildBatchStatus.ReadyToActivate, batch.Status);
        return batch;
    }

    private static void AssertAtomicFailure<TException>(SeriesCastRebuildBatch batch, Action action)
        where TException : Exception
    {
        var before = Capture(batch);

        Assert.Throws<TException>(action);

        var after = Capture(batch);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Members, after.Members);
    }

    private static BatchSnapshot Capture(SeriesCastRebuildBatch batch) =>
        new(
            batch.Status,
            batch.UpdatedAt,
            batch.Members.Select(member => new MemberSnapshot(
                member.Id,
                member.OwnerId,
                member.SeriesId,
                member.BatchId,
                member.SeriesBookId,
                member.BookId,
                member.MembershipRevision,
                member.StagedNarrationJobId,
                member.PreviousActiveNarrationJobId,
                member.Status)).ToArray());

    private static void AssertMemberSnapshot(MemberInput input, SeriesCastRebuildMember member)
    {
        Assert.Equal(input.Id, member.Id);
        Assert.Equal(input.OwnerId, member.OwnerId);
        Assert.Equal(input.SeriesId, member.SeriesId);
        Assert.Equal(input.BatchId, member.BatchId);
        Assert.Equal(input.SeriesBookId, member.SeriesBookId);
        Assert.Equal(input.BookId, member.BookId);
        Assert.Equal(input.MembershipRevision, member.MembershipRevision);
        Assert.Equal(input.PreviousActiveNarrationJobId, member.PreviousActiveNarrationJobId);
        Assert.Equal(SeriesCastRebuildMemberStatus.Pending, member.Status);
        Assert.Null(member.StagedNarrationJobId);
    }

    private static void AssertInternalMethod(string methodName)
    {
        var method = typeof(SeriesCastRebuildBatch).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.True(method.IsAssembly, $"{methodName} must remain narrowly internal.");
    }

    private static void InvokeInternal(
        SeriesCastRebuildBatch batch,
        string methodName,
        DateTimeOffset now)
    {
        var method = typeof(SeriesCastRebuildBatch).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.True(method.IsAssembly, $"{methodName} must remain narrowly internal.");
        try
        {
            method.Invoke(batch, [now]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static CohortFixture CreateFixture()
    {
        var ownerId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var first = new MemberInput(
            Guid.NewGuid(),
            ownerId,
            seriesId,
            batchId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            Guid.NewGuid());
        var second = new MemberInput(
            Guid.NewGuid(),
            ownerId,
            seriesId,
            batchId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            7,
            null);
        var batch = new BatchInput(
            batchId,
            ownerId,
            seriesId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            7,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            [first.Create(), second.Create()]);
        return new CohortFixture(batch, first, second);
    }

    private sealed record CohortFixture(BatchInput Batch, MemberInput First, MemberInput Second);

    private sealed record BatchInput(
        Guid Id,
        Guid OwnerId,
        Guid SeriesId,
        Guid? BaseActiveCastRevisionId,
        Guid DraftCastRevisionId,
        int CohortMembershipRevision,
        DateTimeOffset CreatedAt,
        IReadOnlyCollection<SeriesCastRebuildMember> Members)
    {
        internal SeriesCastRebuildBatch Create() => SeriesCastRebuildBatch.Create(
            Id,
            OwnerId,
            SeriesId,
            BaseActiveCastRevisionId,
            DraftCastRevisionId,
            CohortMembershipRevision,
            CreatedAt,
            Members);
    }

    private sealed record MemberInput(
        Guid Id,
        Guid OwnerId,
        Guid SeriesId,
        Guid BatchId,
        Guid SeriesBookId,
        Guid BookId,
        int MembershipRevision,
        Guid? PreviousActiveNarrationJobId)
    {
        internal SeriesCastRebuildMember Create() => SeriesCastRebuildMember.Create(
            Id,
            OwnerId,
            SeriesId,
            BatchId,
            SeriesBookId,
            BookId,
            MembershipRevision,
            PreviousActiveNarrationJobId);
    }

    private sealed record BatchSnapshot(
        SeriesCastRebuildBatchStatus Status,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<MemberSnapshot> Members);

    private sealed record MemberSnapshot(
        Guid Id,
        Guid OwnerId,
        Guid SeriesId,
        Guid BatchId,
        Guid SeriesBookId,
        Guid BookId,
        int MembershipRevision,
        Guid? StagedNarrationJobId,
        Guid? PreviousActiveNarrationJobId,
        SeriesCastRebuildMemberStatus Status);
}

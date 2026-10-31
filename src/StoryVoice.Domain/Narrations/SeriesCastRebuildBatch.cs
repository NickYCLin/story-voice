namespace StoryVoice.Domain.Narrations;

public enum SeriesCastRebuildBatchStatus
{
    Draft,
    Building,
    ReadyToActivate,
    Activated,
    Failed
}

public sealed class SeriesCastRebuildBatch
{
    private readonly List<SeriesCastRebuildMember> _members = [];
    private SeriesCastRebuildBatchStatus _status;
    private DateTimeOffset _updatedAt;

    private SeriesCastRebuildBatch()
    {
    }

    private SeriesCastRebuildBatch(
        Guid id,
        Guid ownerId,
        Guid seriesId,
        Guid? baseActiveCastRevisionId,
        Guid draftCastRevisionId,
        int cohortMembershipRevision,
        DateTimeOffset createdAt,
        IReadOnlyCollection<SeriesCastRebuildMember> members)
    {
        Id = id;
        OwnerId = ownerId;
        SeriesId = seriesId;
        BaseActiveCastRevisionId = baseActiveCastRevisionId;
        DraftCastRevisionId = draftCastRevisionId;
        CohortMembershipRevision = cohortMembershipRevision;
        CreatedAt = createdAt;
        _updatedAt = createdAt;
        _members.AddRange(members);
        _status = SeriesCastRebuildBatchStatus.Draft;
    }

    public Guid Id { get; }
    public Guid OwnerId { get; }
    public Guid SeriesId { get; }
    public Guid? BaseActiveCastRevisionId { get; }
    public Guid DraftCastRevisionId { get; }
    public int CohortMembershipRevision { get; }
    public SeriesCastRebuildBatchStatus Status => _status;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt => _updatedAt;
    public IReadOnlyList<SeriesCastRebuildMember> Members => _members.AsReadOnly();

    public static SeriesCastRebuildBatch Create(
        Guid id,
        Guid ownerId,
        Guid seriesId,
        Guid? baseActiveCastRevisionId,
        Guid draftCastRevisionId,
        int cohortMembershipRevision,
        DateTimeOffset createdAt,
        IEnumerable<SeriesCastRebuildMember> members)
    {
        EnsureId(id, nameof(id));
        EnsureId(ownerId, nameof(ownerId));
        EnsureId(seriesId, nameof(seriesId));
        EnsureNullableId(baseActiveCastRevisionId, nameof(baseActiveCastRevisionId));
        EnsureId(draftCastRevisionId, nameof(draftCastRevisionId));
        if (baseActiveCastRevisionId == draftCastRevisionId)
        {
            throw new ArgumentException(
                "Draft cast revision 必須與 base active cast revision 不同。",
                nameof(draftCastRevisionId));
        }

        if (cohortMembershipRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cohortMembershipRevision),
                "批次 cohort 成員版本必須大於零。");
        }

        ArgumentNullException.ThrowIfNull(members);
        var memberSnapshot = members.ToArray();
        if (memberSnapshot.Length == 0)
        {
            throw new ArgumentException("重建批次至少必須包含一位 cohort 成員。", nameof(members));
        }

        if (memberSnapshot.Any(member => member is null))
        {
            throw new ArgumentException("重建批次 cohort 不可包含空值。", nameof(members));
        }

        foreach (var member in memberSnapshot)
        {
            if (member.OwnerId != ownerId)
            {
                throw new InvalidOperationException("批次與所有 cohort 成員必須屬於同一位擁有者。");
            }

            if (member.SeriesId != seriesId)
            {
                throw new InvalidOperationException("批次與所有 cohort 成員必須屬於同一系列。");
            }

            if (member.BatchId != id)
            {
                throw new InvalidOperationException("所有 cohort 成員都必須指向正在建立的批次。");
            }

            if (member.Status != SeriesCastRebuildMemberStatus.Pending ||
                member.StagedNarrationJobId is not null)
            {
                throw new InvalidOperationException("新建批次的所有 cohort 成員都必須是未附加工作的 Pending 快照。");
            }
        }

        EnsureUnique(memberSnapshot, member => member.Id, "同一批次內的成員識別碼不可重複。");
        EnsureUnique(memberSnapshot, member => member.SeriesBookId, "同一批次內的 SeriesBook 識別碼不可重複。");
        EnsureUnique(memberSnapshot, member => member.BookId, "同一批次內的 Book 識別碼不可重複。");

        if (memberSnapshot.Max(member => member.MembershipRevision) != cohortMembershipRevision)
        {
            throw new InvalidOperationException("批次 cohort 成員版本必須等於成員版本快照的最大值。");
        }

        var defensiveMemberSnapshot = memberSnapshot
            .Select(member => SeriesCastRebuildMember.Create(
                member.Id,
                member.OwnerId,
                member.SeriesId,
                member.BatchId,
                member.SeriesBookId,
                member.BookId,
                member.MembershipRevision,
                member.PreviousActiveNarrationJobId))
            .ToArray();

        return new SeriesCastRebuildBatch(
            id,
            ownerId,
            seriesId,
            baseActiveCastRevisionId,
            draftCastRevisionId,
            cohortMembershipRevision,
            createdAt,
            defensiveMemberSnapshot);
    }

    public void StartBuilding(DateTimeOffset now)
    {
        if (_status != SeriesCastRebuildBatchStatus.Draft)
        {
            throw new InvalidOperationException("只有 Draft 重建批次可以開始建立。");
        }

        EnsureTransitionTime(now);

        _status = SeriesCastRebuildBatchStatus.Building;
        _updatedAt = now;
    }

    public void AttachStagedJob(Guid seriesBookId, Guid stagedJobId)
    {
        if (_status != SeriesCastRebuildBatchStatus.Building)
        {
            throw new InvalidOperationException("只有 Building 重建批次可以附加 staged narration job。");
        }

        EnsureId(stagedJobId, nameof(stagedJobId));
        var member = FindMember(seriesBookId);
        if (member.Status != SeriesCastRebuildMemberStatus.Pending ||
            member.StagedNarrationJobId is not null)
        {
            throw new InvalidOperationException("目標成員必須是尚未附加工作的 Pending 狀態。");
        }

        if (_members.Any(candidate => candidate.StagedNarrationJobId == stagedJobId))
        {
            throw new InvalidOperationException("同一批次內的 staged narration job 識別碼不可重複。");
        }

        if (member.PreviousActiveNarrationJobId == stagedJobId)
        {
            throw new InvalidOperationException("Staged narration job 不可與該成員先前啟用中的 narration job 相同。");
        }

        var now = DateTimeOffset.UtcNow;
        EnsureTransitionTime(now);

        member.AttachStagedJob(stagedJobId);
        _updatedAt = now;
    }

    public void MarkMemberReady(Guid seriesBookId, DateTimeOffset now)
    {
        if (_status != SeriesCastRebuildBatchStatus.Building)
        {
            throw new InvalidOperationException("只有 Building 重建批次可以將成員標記為 Ready。");
        }

        var member = FindMember(seriesBookId);
        if (member.Status != SeriesCastRebuildMemberStatus.Building ||
            member.StagedNarrationJobId is null)
        {
            throw new InvalidOperationException("只有已附加 staged narration job 的 Building 成員可以標記為 Ready。");
        }

        EnsureTransitionTime(now);
        var allMembersWillBeReady = _members.All(candidate =>
            ReferenceEquals(candidate, member) || candidate.Status == SeriesCastRebuildMemberStatus.Ready);

        member.MarkReady();
        if (allMembersWillBeReady)
        {
            _status = SeriesCastRebuildBatchStatus.ReadyToActivate;
        }

        _updatedAt = now;
    }

    public void MarkMemberFailed(Guid seriesBookId, DateTimeOffset now)
    {
        if (_status != SeriesCastRebuildBatchStatus.Building)
        {
            throw new InvalidOperationException("只有 Building 重建批次可以將成員標記為 Failed。");
        }

        var member = FindMember(seriesBookId);
        if (member.Status is not (SeriesCastRebuildMemberStatus.Pending or SeriesCastRebuildMemberStatus.Building))
        {
            throw new InvalidOperationException("只有 Pending 或 Building 成員可以標記為 Failed。");
        }

        EnsureTransitionTime(now);

        member.MarkFailed();
        _status = SeriesCastRebuildBatchStatus.Failed;
        _updatedAt = now;
    }

    public void ResumeFailed(IReadOnlySet<Guid> completedJobIds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(completedJobIds);
        if (_status != SeriesCastRebuildBatchStatus.Failed
            || _members.Any(member => member.StagedNarrationJobId is null)
            || completedJobIds.Any(id => _members.All(member => member.StagedNarrationJobId != id)))
            throw new InvalidOperationException("只有成員工作完整的失敗批次可以恢復。");
        EnsureTransitionTime(now);
        foreach (var member in _members)
            member.Resume(completedJobIds.Contains(member.StagedNarrationJobId!.Value));
        _status = _members.All(member => member.Status == SeriesCastRebuildMemberStatus.Ready)
            ? SeriesCastRebuildBatchStatus.ReadyToActivate : SeriesCastRebuildBatchStatus.Building;
        _updatedAt = now;
    }

    /// <summary>
    /// Reconciles a persisted terminal member snapshot after a worker retry or concurrent completion.
    /// It deliberately accepts members that are already terminal so a lost final batch transition
    /// can be repaired without mutating the members again.
    /// </summary>
    public bool ReconcileTerminalMemberState(DateTimeOffset now)
    {
        if (_status != SeriesCastRebuildBatchStatus.Building)
        {
            return false;
        }

        if (_members.Any(candidate => candidate.Status == SeriesCastRebuildMemberStatus.Failed))
        {
            EnsureTransitionTime(now);
            _status = SeriesCastRebuildBatchStatus.Failed;
            _updatedAt = now;
            return true;
        }

        if (_members.All(candidate => candidate.Status == SeriesCastRebuildMemberStatus.Ready))
        {
            EnsureTransitionTime(now);
            _status = SeriesCastRebuildBatchStatus.ReadyToActivate;
            _updatedAt = now;
            return true;
        }

        return false;
    }

    internal void MarkActivated(DateTimeOffset now)
    {
        if (_status != SeriesCastRebuildBatchStatus.ReadyToActivate)
        {
            throw new InvalidOperationException("只有 ReadyToActivate 重建批次可以標記為 Activated。");
        }

        EnsureTransitionTime(now);

        _status = SeriesCastRebuildBatchStatus.Activated;
        _updatedAt = now;
    }

    internal void Invalidate(DateTimeOffset now)
    {
        if (_status is not (
            SeriesCastRebuildBatchStatus.Draft or
            SeriesCastRebuildBatchStatus.Building or
            SeriesCastRebuildBatchStatus.ReadyToActivate))
        {
            throw new InvalidOperationException("只有尚未 Activated 或 Failed 的重建批次可以失效。");
        }

        EnsureTransitionTime(now);

        _status = SeriesCastRebuildBatchStatus.Failed;
        _updatedAt = now;
    }

    private SeriesCastRebuildMember FindMember(Guid seriesBookId)
    {
        EnsureId(seriesBookId, nameof(seriesBookId));
        var member = _members.SingleOrDefault(candidate => candidate.SeriesBookId == seriesBookId);
        return member ?? throw new InvalidOperationException("找不到指定的 SeriesBook cohort 成員。");
    }

    private void EnsureTransitionTime(DateTimeOffset now)
    {
        if (now < CreatedAt || now < _updatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "轉換時間不可早於建立時間或目前更新時間。");
        }
    }

    private static void EnsureUnique(
        IReadOnlyCollection<SeriesCastRebuildMember> members,
        Func<SeriesCastRebuildMember, Guid> keySelector,
        string message)
    {
        if (members.GroupBy(keySelector).Any(group => group.Skip(1).Any()))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void EnsureId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("識別碼不可為空白。", parameterName);
        }
    }

    private static void EnsureNullableId(Guid? value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("可為空值的識別碼在有值時不可為空白 Guid。", parameterName);
        }
    }
}

namespace StoryVoice.Domain.Narrations;

public enum SeriesCastRebuildMemberStatus
{
    Pending,
    Building,
    Ready,
    Failed
}

public sealed class SeriesCastRebuildMember
{
    private Guid? _stagedNarrationJobId;
    private SeriesCastRebuildMemberStatus _status;

    private SeriesCastRebuildMember()
    {
    }

    private SeriesCastRebuildMember(
        Guid id,
        Guid ownerId,
        Guid seriesId,
        Guid batchId,
        Guid seriesBookId,
        Guid bookId,
        int membershipRevision,
        Guid? previousActiveNarrationJobId)
    {
        Id = id;
        OwnerId = ownerId;
        SeriesId = seriesId;
        BatchId = batchId;
        SeriesBookId = seriesBookId;
        BookId = bookId;
        MembershipRevision = membershipRevision;
        PreviousActiveNarrationJobId = previousActiveNarrationJobId;
        _status = SeriesCastRebuildMemberStatus.Pending;
    }

    public Guid Id { get; }
    public Guid OwnerId { get; }
    public Guid SeriesId { get; }
    public Guid BatchId { get; }
    public Guid SeriesBookId { get; }
    public Guid BookId { get; }
    public int MembershipRevision { get; }
    public Guid? StagedNarrationJobId => _stagedNarrationJobId;
    public Guid? PreviousActiveNarrationJobId { get; }
    public SeriesCastRebuildMemberStatus Status => _status;

    public static SeriesCastRebuildMember Create(
        Guid id,
        Guid ownerId,
        Guid seriesId,
        Guid batchId,
        Guid seriesBookId,
        Guid bookId,
        int membershipRevision,
        Guid? previousActiveNarrationJobId)
    {
        EnsureId(id, nameof(id));
        EnsureId(ownerId, nameof(ownerId));
        EnsureId(seriesId, nameof(seriesId));
        EnsureId(batchId, nameof(batchId));
        EnsureId(seriesBookId, nameof(seriesBookId));
        EnsureId(bookId, nameof(bookId));
        EnsureNullableId(previousActiveNarrationJobId, nameof(previousActiveNarrationJobId));
        if (membershipRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(membershipRevision),
                "成員版本快照必須大於零。");
        }

        return new SeriesCastRebuildMember(
            id,
            ownerId,
            seriesId,
            batchId,
            seriesBookId,
            bookId,
            membershipRevision,
            previousActiveNarrationJobId);
    }

    internal void AttachStagedJob(Guid stagedNarrationJobId)
    {
        if (_status != SeriesCastRebuildMemberStatus.Pending || _stagedNarrationJobId is not null)
        {
            throw new InvalidOperationException("只有尚未建立工作的批次成員可以附加 staged narration job。");
        }

        EnsureId(stagedNarrationJobId, nameof(stagedNarrationJobId));
        if (stagedNarrationJobId == PreviousActiveNarrationJobId)
        {
            throw new InvalidOperationException("Staged narration job 不可與先前啟用中的 narration job 相同。");
        }

        _stagedNarrationJobId = stagedNarrationJobId;
        _status = SeriesCastRebuildMemberStatus.Building;
    }

    internal void MarkReady()
    {
        if (_status != SeriesCastRebuildMemberStatus.Building || _stagedNarrationJobId is null)
        {
            throw new InvalidOperationException("只有已附加 staged narration job 的 Building 成員可以標記為 Ready。");
        }

        _status = SeriesCastRebuildMemberStatus.Ready;
    }

    internal void MarkFailed()
    {
        if (_status is not (SeriesCastRebuildMemberStatus.Pending or SeriesCastRebuildMemberStatus.Building))
        {
            throw new InvalidOperationException("只有 Pending 或 Building 成員可以標記為 Failed。");
        }

        _status = SeriesCastRebuildMemberStatus.Failed;
    }

    internal void Resume(bool completed)
    {
        if (_stagedNarrationJobId is null)
            throw new InvalidOperationException("尚未附加工作的批次成員無法恢復。");
        _status = completed ? SeriesCastRebuildMemberStatus.Ready : SeriesCastRebuildMemberStatus.Building;
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

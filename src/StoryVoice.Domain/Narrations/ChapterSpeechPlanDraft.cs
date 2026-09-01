namespace StoryVoice.Domain.Narrations;

/// <summary>
/// Per-chapter, per-series draft of narrator/dialogue turns awaiting speaker review. Rebuilt from
/// scratch (new <see cref="PlanVersion"/>) whenever the chapter's source hash changes; confirming
/// it freezes the current segments into an immutable <see cref="ConfirmedSpeechPlanRevision"/>.
/// </summary>
public sealed class ChapterSpeechPlanDraft
{
    private readonly List<SpeechSegmentDraft> _segments = [];

    private ChapterSpeechPlanDraft()
    {
    }

    private ChapterSpeechPlanDraft(
        Guid ownerId,
        Guid seriesId,
        Guid bookId,
        Guid chapterId,
        string sourceHash,
        int planVersion)
    {
        EnsureId(ownerId, nameof(ownerId));
        EnsureId(seriesId, nameof(seriesId));
        EnsureId(bookId, nameof(bookId));
        EnsureId(chapterId, nameof(chapterId));
        if (planVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(planVersion), "劇本版本必須從 1 開始。");
        }

        Id = Guid.NewGuid();
        OwnerId = ownerId;
        SeriesId = seriesId;
        BookId = bookId;
        ChapterId = chapterId;
        SourceHash = RequireHash(sourceHash);
        PlanVersion = planVersion;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid SeriesId { get; private set; }
    public Guid BookId { get; private set; }
    public Guid ChapterId { get; private set; }
    public string SourceHash { get; private set; } = string.Empty;
    public int PlanVersion { get; private set; }
    public ChapterSpeechPlanDraftStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<SpeechSegmentDraft> Segments => _segments.AsReadOnly();

    public static ChapterSpeechPlanDraft Create(
        Guid ownerId,
        Guid seriesId,
        Guid bookId,
        Guid chapterId,
        string sourceHash,
        IReadOnlyList<DraftSegmentInput> segments)
    {
        var draft = new ChapterSpeechPlanDraft(ownerId, seriesId, bookId, chapterId, sourceHash, planVersion: 1);
        draft.ReplaceSegments(segments);
        return draft;
    }

    /// <summary>
    /// Rebuilds the draft from new segmenter/attribution output. Exact matching dialogue keeps an
    /// explicit user confirmation or rejection; generated suggestions are replaced.
    /// </summary>
    public void RegenerateFromSegmentation(string newSourceHash, IReadOnlyList<DraftSegmentInput> segments)
    {
        var regeneratedSegments = PreserveUserDialogueDecisions(segments);
        SourceHash = RequireHash(newSourceHash);
        PlanVersion = checked(PlanVersion + 1);
        ReplaceSegments(regeneratedSegments);
    }

    public void ConfirmSegment(Guid segmentId, Guid? characterId)
    {
        EnsureNotStale();
        var segment = FindSegment(segmentId);
        if (segment.Kind != SpeechSegmentTurnKind.Dialogue)
        {
            throw new InvalidOperationException("旁白片段不需要（也不可以）人工確認角色。");
        }

        segment.Confirm(characterId);
        RecomputeStatus();
        Touch();
    }

    /// <summary>
    /// Confirms every dialogue segment that still carries an unreviewed suggestion with an actual
    /// character attached. Suggestions without a character (unknown speakers) are deliberately
    /// left for manual review — bulk-accept must never silently turn unknowns into narrator turns.
    /// When <paramref name="characterId"/> is provided, only suggestions pointing at that
    /// character are confirmed, so one badly attributed name does not ride along with the rest.
    /// </summary>
    public int ConfirmSuggestedSegments(Guid? characterId)
    {
        EnsureNotStale();
        var suggested = _segments
            .Where(segment => segment.Kind == SpeechSegmentTurnKind.Dialogue
                && segment.ReviewStatus == SpeechSegmentReviewStatus.Suggested
                && segment.CharacterId is not null
                && (characterId is null || segment.CharacterId == characterId))
            .ToArray();
        foreach (var segment in suggested)
        {
            segment.Confirm(segment.CharacterId);
        }

        if (suggested.Length > 0)
        {
            RecomputeStatus();
            Touch();
        }

        return suggested.Length;
    }

    /// <summary>
    /// User override of a body segment's turn kind (narrator / dialogue / inner monologue) and
    /// speaker. This is the correction path for segmenter misreads and for auto-confirmed rule
    /// decisions that turned out to be wrong.
    /// </summary>
    public void ReassignSegment(Guid segmentId, SpeechSegmentTurnKind kind, Guid? characterId)
    {
        EnsureNotStale();
        var segment = FindSegment(segmentId);
        segment.Reassign(kind, characterId);
        RecomputeStatus();
        Touch();
    }

    public void RejectSegment(Guid segmentId)
    {
        EnsureNotStale();
        var segment = FindSegment(segmentId);
        if (segment.Kind != SpeechSegmentTurnKind.Dialogue)
        {
            throw new InvalidOperationException("旁白片段沒有可拒絕的角色建議。");
        }

        segment.Reject();
        RecomputeStatus();
        Touch();
    }

    /// <summary>
    /// Marks the draft stale because the chapter's title/body no longer matches
    /// <see cref="SourceHash"/>. A stale draft cannot be reviewed or confirmed until it is
    /// rebuilt via <see cref="RegenerateFromSegmentation"/>.
    /// </summary>
    public void MarkStale()
    {
        if (Status == ChapterSpeechPlanDraftStatus.Stale)
        {
            return;
        }

        Status = ChapterSpeechPlanDraftStatus.Stale;
        Touch();
    }

    public ConfirmedSpeechPlanRevision Confirm(int revisionNumber, DateTimeOffset now)
    {
        if (Status != ChapterSpeechPlanDraftStatus.ReadyToConfirm)
        {
            throw new InvalidOperationException("只有所有對白片段都已確認的劇本草稿可以確認為正式版本。");
        }

        return ConfirmedSpeechPlanRevision.Create(
            OwnerId,
            SeriesId,
            BookId,
            ChapterId,
            revisionNumber,
            SourceHash,
            now,
            _segments);
    }

    private void ReplaceSegments(IReadOnlyList<DraftSegmentInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw new ArgumentException("章節劇本草稿至少要有一個片段。", nameof(inputs));
        }

        var first = inputs[0];
        if (first.SortOrder != 0
            || first.SourceKind != SpeechSegmentSourceKind.ChapterTitle
            || first.Kind is not (
                SpeechSegmentTurnKind.Narrator or
                SpeechSegmentTurnKind.InnerMonologue))
        {
            throw new ArgumentException("第一個片段必須是章名的旁白 turn。", nameof(inputs));
        }

        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            if (input.SortOrder != index)
            {
                throw new ArgumentException("片段排序必須從 0 開始連續遞增，不可缺漏或重複。", nameof(inputs));
            }

            if (index > 0 && input.SourceKind == SpeechSegmentSourceKind.ChapterTitle)
            {
                throw new ArgumentException("章名片段只能出現一次，且必須是第一個片段。", nameof(inputs));
            }
        }

        var segments = inputs
            .Select(input => SpeechSegmentDraft.Create(OwnerId, SeriesId, Id, input))
            .ToArray();

        _segments.Clear();
        _segments.AddRange(segments);
        RecomputeStatus();
    }

    private IReadOnlyList<DraftSegmentInput> PreserveUserDialogueDecisions(
        IReadOnlyList<DraftSegmentInput> regeneratedSegments)
    {
        ArgumentNullException.ThrowIfNull(regeneratedSegments);

        var userDecisions = _segments
            .Where(segment => segment.Kind == SpeechSegmentTurnKind.Dialogue
                && segment.DecisionSource == SpeechSegmentDecisionSource.User
                && segment.ReviewStatus is SpeechSegmentReviewStatus.Confirmed
                    or SpeechSegmentReviewStatus.Rejected)
            .ToDictionary(
                segment => new DialogueMatchKey(
                    segment.SourceKind,
                    segment.StartOffset,
                    segment.Length,
                    segment.TextHash));

        return regeneratedSegments
            .Select(segment =>
            {
                if (segment.Kind != SpeechSegmentTurnKind.Dialogue
                    || !userDecisions.TryGetValue(
                        new DialogueMatchKey(
                            segment.SourceKind,
                            segment.StartOffset,
                            segment.Length,
                            segment.TextHash),
                        out var userDecision))
                {
                    return segment;
                }

                return segment with
                {
                    CharacterId = userDecision.CharacterId,
                    Confidence = userDecision.Confidence,
                    DecisionSource = SpeechSegmentDecisionSource.User,
                    ReviewStatus = userDecision.ReviewStatus,
                };
            })
            .ToArray();
    }

    private void RecomputeStatus()
    {
        var dialogueSegments = _segments.Where(segment => segment.Kind == SpeechSegmentTurnKind.Dialogue).ToArray();
        Status = dialogueSegments.All(segment => segment.ReviewStatus == SpeechSegmentReviewStatus.Confirmed)
            ? ChapterSpeechPlanDraftStatus.ReadyToConfirm
            : ChapterSpeechPlanDraftStatus.NeedsReview;
    }

    private SpeechSegmentDraft FindSegment(Guid segmentId) =>
        _segments.SingleOrDefault(segment => segment.Id == segmentId)
            ?? throw new InvalidOperationException("片段不屬於這份劇本草稿。");

    private void EnsureNotStale()
    {
        if (Status == ChapterSpeechPlanDraftStatus.Stale)
        {
            throw new InvalidOperationException("劇本草稿已過期，請先依最新章節內容重新產生後再審核。");
        }
    }

    private void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string RequireHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 1 or > 128)
        {
            throw new ArgumentException("來源雜湊不可為空白，長度必須介於 1 與 128 之間。", nameof(value));
        }

        return value;
    }

    private static void EnsureId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("識別碼不可為空白。", parameterName);
        }
    }

    private readonly record struct DialogueMatchKey(
        SpeechSegmentSourceKind SourceKind,
        int StartOffset,
        int Length,
        string TextHash);
}

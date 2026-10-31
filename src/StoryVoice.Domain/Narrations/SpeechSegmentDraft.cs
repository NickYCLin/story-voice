namespace StoryVoice.Domain.Narrations;

/// <summary>
/// One narrator, dialogue or inner-monologue turn inside a <see cref="ChapterSpeechPlanDraft"/>. Only offsets and
/// a text hash are stored — never the source text itself — matching the same "no duplicated
/// private text" contract as the Task 5 segmenter output.
/// </summary>
public sealed class SpeechSegmentDraft
{
    private SpeechSegmentDraft()
    {
    }

    private SpeechSegmentDraft(
        Guid ownerId,
        Guid seriesId,
        Guid planDraftId,
        int sortOrder,
        SpeechSegmentSourceKind sourceKind,
        int startOffset,
        int length,
        string textHash,
        SpeechSegmentTurnKind kind,
        Guid? characterId,
        int confidence,
        SpeechSegmentDecisionSource decisionSource,
        SpeechSegmentReviewStatus reviewStatus)
    {
        EnsureId(ownerId, nameof(ownerId));
        EnsureId(seriesId, nameof(seriesId));
        EnsureId(planDraftId, nameof(planDraftId));
        if (sortOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder), "片段排序不可為負數。");
        }

        if (startOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset), "片段起始位置不可為負數。");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "片段長度必須大於零。");
        }

        if (confidence is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "信心分數必須介於 0 與 100 之間。");
        }

        if (kind == SpeechSegmentTurnKind.Narrator && characterId is not null)
        {
            throw new ArgumentException("旁白片段不可指定角色。", nameof(characterId));
        }

        if (kind == SpeechSegmentTurnKind.Dialogue
            && sourceKind != SpeechSegmentSourceKind.Body)
        {
            throw new ArgumentException("對白片段必須來自章節正文，不可以是章名。", nameof(sourceKind));
        }

        if (kind == SpeechSegmentTurnKind.InnerMonologue && characterId is null)
        {
            throw new ArgumentException("內心／默讀片段必須指定視角角色。", nameof(characterId));
        }

        if (kind == SpeechSegmentTurnKind.InnerMonologue
            && (confidence != 100
                || decisionSource != SpeechSegmentDecisionSource.Rule
                || reviewStatus != SpeechSegmentReviewStatus.Confirmed))
        {
            throw new ArgumentException("內心／默讀片段必須是規則產生、100% 信心且已確認的結果。", nameof(reviewStatus));
        }

        Id = Guid.NewGuid();
        OwnerId = ownerId;
        SeriesId = seriesId;
        PlanDraftId = planDraftId;
        SortOrder = sortOrder;
        SourceKind = sourceKind;
        StartOffset = startOffset;
        Length = length;
        TextHash = RequireHash(textHash);
        Kind = kind;
        CharacterId = characterId;
        Confidence = confidence;
        DecisionSource = decisionSource;
        ReviewStatus = reviewStatus;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid SeriesId { get; private set; }
    public Guid PlanDraftId { get; private set; }
    public int SortOrder { get; private set; }
    public SpeechSegmentSourceKind SourceKind { get; private set; }
    public int StartOffset { get; private set; }
    public int Length { get; private set; }
    public string TextHash { get; private set; } = string.Empty;
    public SpeechSegmentTurnKind Kind { get; private set; }
    public Guid? CharacterId { get; private set; }
    public int Confidence { get; private set; }
    public SpeechSegmentDecisionSource DecisionSource { get; private set; }
    public SpeechSegmentReviewStatus ReviewStatus { get; private set; }

    internal static SpeechSegmentDraft Create(
        Guid ownerId,
        Guid seriesId,
        Guid planDraftId,
        DraftSegmentInput input) =>
        new(
            ownerId,
            seriesId,
            planDraftId,
            input.SortOrder,
            input.SourceKind,
            input.StartOffset,
            input.Length,
            input.TextHash,
            input.Kind,
            input.CharacterId,
            input.Confidence,
            input.DecisionSource,
            input.ReviewStatus);

    internal void Confirm(Guid? characterId)
    {
        CharacterId = characterId;
        Confidence = 100;
        DecisionSource = SpeechSegmentDecisionSource.User;
        ReviewStatus = SpeechSegmentReviewStatus.Confirmed;
    }

    internal void Reject()
    {
        CharacterId = null;
        Confidence = 0;
        DecisionSource = SpeechSegmentDecisionSource.User;
        ReviewStatus = SpeechSegmentReviewStatus.Rejected;
    }

    /// <summary>
    /// Explicit user override of the segmenter's turn kind. The segmenter can misread unusual
    /// quoting (dashes, unclosed quotes) as narration, or promote narration to dialogue; this is
    /// the correction path. Chapter-title turns keep their derived kind and cannot be reassigned.
    /// </summary>
    internal void Reassign(SpeechSegmentTurnKind kind, Guid? characterId)
    {
        if (SourceKind != SpeechSegmentSourceKind.Body)
        {
            throw new InvalidOperationException("章名片段的朗讀方式由系列敘事設定決定，不可個別改指派。");
        }

        if (kind == SpeechSegmentTurnKind.Narrator && characterId is not null)
        {
            throw new ArgumentException("旁白片段不可指定角色。", nameof(characterId));
        }

        if (kind == SpeechSegmentTurnKind.InnerMonologue && characterId is null)
        {
            throw new ArgumentException("內心／默讀片段必須指定角色。", nameof(characterId));
        }

        Kind = kind;
        CharacterId = characterId;
        Confidence = 100;
        DecisionSource = SpeechSegmentDecisionSource.User;
        ReviewStatus = SpeechSegmentReviewStatus.Confirmed;
    }

    private static string RequireHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 1 or > 128)
        {
            throw new ArgumentException("文字雜湊不可為空白，長度必須介於 1 與 128 之間。", nameof(value));
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
}

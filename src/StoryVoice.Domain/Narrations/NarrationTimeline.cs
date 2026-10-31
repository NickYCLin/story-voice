namespace StoryVoice.Domain.Narrations;

/// <summary>
/// The per-turn playback timeline captured while a narration job's audio was composed, stored as
/// one JSON document per job. Only offsets into the job's locked chapter text are stored — never
/// the text itself — so the document stays valid to keep even when the source book is later
/// edited (the read side re-verifies the job's source hash before slicing any text back out).
/// A missing row simply means the job's provider could not report timing; playback still works.
/// </summary>
public sealed class NarrationTimeline
{
    private NarrationTimeline()
    {
    }

    private NarrationTimeline(Guid ownerId, Guid narrationJobId, string timelineJson)
    {
        if (ownerId == Guid.Empty || narrationJobId == Guid.Empty)
        {
            throw new ArgumentException("時間軸的擁有者與朗讀工作識別碼不可為空白。");
        }

        if (string.IsNullOrWhiteSpace(timelineJson))
        {
            throw new ArgumentException("時間軸內容不可為空白。", nameof(timelineJson));
        }

        NarrationJobId = narrationJobId;
        OwnerId = ownerId;
        TimelineJson = timelineJson;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid NarrationJobId { get; private set; }
    public Guid OwnerId { get; private set; }
    public string TimelineJson { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public static NarrationTimeline Create(Guid ownerId, Guid narrationJobId, string timelineJson) =>
        new(ownerId, narrationJobId, timelineJson);
}

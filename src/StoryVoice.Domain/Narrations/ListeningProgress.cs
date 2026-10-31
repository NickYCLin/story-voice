namespace StoryVoice.Domain.Narrations;

public sealed class ListeningProgress
{
    public const long MaximumDurationMs = 30L * 24 * 60 * 60 * 1000;

    private ListeningProgress() { }

    public Guid NarrationJobId { get; private set; }
    public Guid OwnerId { get; private set; }
    public long PositionMs { get; private set; }
    public long DurationMs { get; private set; }
    public Guid Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ListeningProgress Create(Guid ownerId, Guid jobId, long positionMs, long durationMs)
    {
        if (ownerId == Guid.Empty || jobId == Guid.Empty)
            throw new ArgumentException("播放進度的擁有者與音檔識別碼不可為空白。");

        var progress = new ListeningProgress { OwnerId = ownerId, NarrationJobId = jobId };
        progress.Update(positionMs, durationMs);
        return progress;
    }

    public void Update(long positionMs, long durationMs)
    {
        if (durationMs <= 0 || durationMs > MaximumDurationMs || positionMs < 0 || positionMs > durationMs)
            throw new ArgumentOutOfRangeException(nameof(positionMs), "播放位置或音檔長度不正確。");

        PositionMs = positionMs;
        DurationMs = durationMs;
        Version = Guid.NewGuid();
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

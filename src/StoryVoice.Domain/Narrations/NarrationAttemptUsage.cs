namespace StoryVoice.Domain.Narrations;

/// <summary>Operational usage for one worker claim. These quantities are not billable units.</summary>
public sealed class NarrationAttemptUsage
{
    private NarrationAttemptUsage() { }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid NarrationJobId { get; private set; }
    public string LeaseOwner { get; private set; } = string.Empty;
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string Outcome { get; private set; } = "Running";
    public string? Provider { get; private set; }
    public long? InputCharacters { get; private set; }
    public int? CompletedChunks { get; private set; }
    public int? TotalChunks { get; private set; }
    public long? ElapsedMs { get; private set; }
    public long? SynthesisElapsedMs { get; private set; }
    public long? AudioBytes { get; private set; }

    public static NarrationAttemptUsage Start(Guid id, Guid ownerId, Guid jobId, string leaseOwner)
    {
        if (id == Guid.Empty || ownerId == Guid.Empty || jobId == Guid.Empty)
            throw new ArgumentException("配音用量紀錄的識別碼不可為空白。");
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 200)
            throw new ArgumentException("配音工作租約格式不正確。", nameof(leaseOwner));
        return new NarrationAttemptUsage
        {
            Id = id, OwnerId = ownerId, NarrationJobId = jobId, LeaseOwner = leaseOwner,
            StartedAt = DateTimeOffset.UtcNow,
        };
    }
}

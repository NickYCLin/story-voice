namespace StoryVoice.Domain.Narrations;

/// <summary>Append-only private listening choice; never changes a cast epoch or job visibility.</summary>
public sealed class NarrationTrackSelection
{
    private NarrationTrackSelection() { }
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid BookId { get; private set; }
    public Guid NarrationJobId { get; private set; }
    public Guid? PreviousNarrationJobId { get; private set; }
    public long Revision { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public static NarrationTrackSelection Create(Guid ownerId, Guid bookId, Guid jobId, Guid? previousJobId, long revision)
    {
        if (ownerId == Guid.Empty || bookId == Guid.Empty || jobId == Guid.Empty || revision < 1)
            throw new ArgumentException("音軌選擇的識別碼與版本不正確。");
        return new() { Id = Guid.NewGuid(), OwnerId = ownerId, BookId = bookId, NarrationJobId = jobId,
            PreviousNarrationJobId = previousJobId, Revision = revision, CreatedAt = DateTimeOffset.UtcNow };
    }
}

namespace StoryVoice.Application.Narrations;

public sealed record NarrationTrackVersion(Guid JobId, string Mode, string Visibility, string Status,
    Guid? CastRevisionId, Guid? RebuildBatchId, string SourceHash, Guid ArtifactVersion,
    long? AudioBytes, DateTimeOffset CreatedAt, bool Available, string? BlockedReason);
public sealed record NarrationTrackState(Guid BookId, long Revision, Guid? SelectedJobId,
    IReadOnlyList<NarrationTrackVersion> Tracks);
public sealed record SelectNarrationTrackRequest(Guid JobId, long ExpectedRevision, Guid ExpectedArtifactVersion);
public sealed record SelectNarrationTrackResult(NarrationTrackState State, bool Conflict);
public interface INarrationTrackService
{
    Task<NarrationTrackState?> ListAsync(Guid bookId, CancellationToken cancellationToken);
    Task<SelectNarrationTrackResult?> SelectAsync(Guid bookId, SelectNarrationTrackRequest request, CancellationToken cancellationToken);
    Task<NarrationAudioDescriptor?> GetPrivateAudioAsync(Guid bookId, Guid jobId, CancellationToken cancellationToken);
}

namespace StoryVoice.Application.Narrations;

public interface INarrationService
{
    Task<IReadOnlyList<NarrationJobResponse>?> ListAsync(Guid bookId, CancellationToken cancellationToken);

    Task<NarrationJobResponse?> CreateAsync(
        Guid bookId,
        CreateNarrationRequest request,
        CancellationToken cancellationToken);

    Task<NarrationJobResponse?> GetAsync(Guid jobId, CancellationToken cancellationToken);

    Task<NarrationJobResponse?> CancelAsync(Guid jobId, CancellationToken cancellationToken);

    Task<NarrationAudioDescriptor?> GetAudioAsync(Guid jobId, CancellationToken cancellationToken);

    Task<NarrationTimelineResponse?> GetTimelineAsync(Guid jobId, CancellationToken cancellationToken);
}

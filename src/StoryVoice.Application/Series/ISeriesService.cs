namespace StoryVoice.Application.Series;

public interface ISeriesService
{
    Task<IReadOnlyList<StorySeriesSummaryResponse>> ListAsync(
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> GetAsync(
        Guid seriesId,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse> CreateAsync(
        CreateStorySeriesRequest request,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> AddBookAsync(
        Guid seriesId,
        AddSeriesBookRequest request,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> AddCharacterAsync(
        Guid seriesId,
        AddSeriesCharacterRequest request,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> UpdateCharacterAsync(
        Guid seriesId,
        Guid characterId,
        UpdateSeriesCharacterRequest request,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> SetCharacterProfileAsync(
        Guid seriesId,
        Guid characterId,
        SetSeriesCharacterProfileRequest request,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> AddAliasAsync(
        Guid seriesId,
        Guid characterId,
        AddSeriesCharacterAliasRequest request,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> ApplyAnalyzedCharactersAsync(
        Guid seriesId,
        ApplyAnalyzedSeriesCharactersRequest request,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> SetPointOfViewCharacterAsync(
        Guid seriesId,
        SetSeriesPointOfViewCharacterRequest request,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> ConfigureNarrativeVoiceAsync(
        Guid seriesId,
        ConfigureSeriesNarrativeVoiceRequest request,
        CancellationToken cancellationToken);

    Task<StorySeriesDetailsResponse?> ConfigureVoicesAsync(
        Guid seriesId,
        ConfigureSeriesVoicesRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesVoiceOptionResponse>> ListVoiceOptionsAsync(CancellationToken cancellationToken);
}

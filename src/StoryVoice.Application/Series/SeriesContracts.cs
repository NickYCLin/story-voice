using StoryVoice.Domain.Series;

namespace StoryVoice.Application.Series;

public sealed record CreateStorySeriesRequest(
    string Name,
    string NarratorProvider,
    string NarratorVoice,
    string NarratorRate,
    string NarratorPitch,
    string NarratorVolume,
    int DefaultSpeakerPauseMs);

public sealed record AddSeriesBookRequest(
    Guid BookId,
    string VolumeLabel,
    int SortOrder);

public sealed record AddSeriesCharacterRequest(
    string CanonicalName,
    SeriesCharacterRole Role,
    string VoiceProvider,
    string Voice,
    string Rate,
    string Pitch,
    string Volume,
    string? Notes,
    Guid? CharacterProfileId = null);

public sealed record UpdateSeriesCharacterRequest(
    string CanonicalName,
    SeriesCharacterRole Role,
    string VoiceProvider,
    string Voice,
    string Rate,
    string Pitch,
    string Volume,
    string? Notes,
    Guid? CharacterProfileId = null);

/// <summary>
/// Links an active, owner-scoped library profile, or unlinks with <see langword="null"/>.
/// Repeating an existing link is a no-op even if that profile was later deactivated.
/// </summary>
public sealed record SetSeriesCharacterProfileRequest(Guid? CharacterProfileId);

public sealed record AddSeriesCharacterAliasRequest(string Alias);

public sealed record ApplyAnalyzedSeriesCharacterRequest(
    string SourceName,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    SeriesCharacterRole Role,
    string VoiceProvider,
    string Voice,
    string Rate,
    string Pitch,
    string Volume);

public sealed record ApplyAnalyzedSeriesCharactersRequest(
    Guid BookId,
    IReadOnlyList<ApplyAnalyzedSeriesCharacterRequest> Characters);

public sealed record SetSeriesPointOfViewCharacterRequest(Guid? CharacterId);

public sealed record ConfigureSeriesNarrativeVoiceRequest(
    NarrativeVoiceMode Mode,
    Guid? PointOfViewCharacterId);

/// <summary>
/// Atomically replaces the narrator and every existing character's synthesis voice. Requiring a
/// complete cast prevents a provider switch from leaving a half-migrated series that cannot be
/// staged by a single multi-voice provider.
/// </summary>
public sealed record ConfigureSeriesVoicesRequest(
    string NarratorProvider,
    string NarratorVoice,
    IReadOnlyList<ConfigureSeriesCharacterVoiceRequest> Characters);

public sealed record ConfigureSeriesCharacterVoiceRequest(
    Guid CharacterId,
    string VoiceProvider,
    string Voice);

public sealed record SeriesVoiceOptionResponse(
    string Provider,
    string Voice,
    string DisplayName,
    string Locale,
    bool FormalNarrationAvailable,
    string UsageScope,
    string? Gender = null,
    int? MinimumAge = null,
    int? MaximumAge = null,
    IReadOnlyList<string>? CharacterTags = null);

public sealed record StorySeriesSummaryResponse(
    Guid Id,
    string Name,
    string NarratorProvider,
    int BookCount,
    int CharacterCount,
    Guid? ActiveCastRevisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record StorySeriesBookResponse(
    Guid Id,
    Guid BookId,
    string BookTitle,
    string VolumeLabel,
    int SortOrder,
    int MembershipRevision,
    Guid? ActiveNarrationJobId);

public sealed record StorySeriesAliasResponse(
    Guid Id,
    string Value);

public sealed record StorySeriesCharacterResponse(
    Guid Id,
    string CanonicalName,
    string Role,
    string VoiceProvider,
    string Voice,
    string Rate,
    string Pitch,
    string Volume,
    string? Notes,
    Guid? CharacterProfileId,
    IReadOnlyList<StorySeriesAliasResponse> Aliases,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record StorySeriesDetailsResponse(
    Guid Id,
    string Name,
    string NarratorProvider,
    string NarratorVoice,
    string NarratorRate,
    string NarratorPitch,
    string NarratorVolume,
    int DefaultSpeakerPauseMs,
    Guid? ActiveCastRevisionId,
    Guid? PointOfViewCharacterId,
    string NarrativeVoiceMode,
    IReadOnlyList<StorySeriesBookResponse> Books,
    IReadOnlyList<StorySeriesCharacterResponse> Characters,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

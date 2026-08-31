namespace StoryVoice.Application.Characters;

public sealed record CreateCharacterProfileRequest(
    string CanonicalName,
    string? Age,
    string? Gender,
    string? Birthday,
    string? Personality,
    string? Catchphrase,
    string? Background,
    string? SpeakingStyle);

public sealed record UpdateCharacterProfileRequest(
    string CanonicalName,
    string? Age,
    string? Gender,
    string? Birthday,
    string? Personality,
    string? Catchphrase,
    string? Background,
    string? SpeakingStyle);

public sealed record CharacterProfileResponse(
    Guid Id,
    string CanonicalName,
    bool HasAvatar,
    string? Age,
    string? Gender,
    string? Birthday,
    string? Personality,
    string? Catchphrase,
    string? Background,
    string? SpeakingStyle,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CharacterProfileAvatar(string AbsolutePath, string ContentType);

public sealed record GenerateCharacterProfileAssistRequest(
    string CanonicalName,
    string? Gender,
    string? Age,
    string? ExistingPersonality,
    string? ExistingBackground,
    string? ExistingCatchphrase,
    string? ExistingSpeakingStyle,
    string? FieldToGenerate);

public sealed record GeneratedCharacterProfileAssistResponse(
    string? Personality,
    string? Background,
    string? SpeakingStyle,
    string? Catchphrase);

/// <summary>
/// The character library: owner-scoped character identities that are reusable across every
/// series, independent of <see cref="StoryVoice.Application.Series.ISeriesService"/>. A series
/// character can optionally link to one of these (<c>SeriesCharacter.CharacterProfileId</c>) to
/// inherit its voice profiles instead of building a series-only fixed voice.
/// </summary>
public interface ICharacterProfileService
{
    Task<IReadOnlyList<CharacterProfileResponse>> ListAsync(CancellationToken cancellationToken);

    Task<CharacterProfileResponse?> GetAsync(Guid characterProfileId, CancellationToken cancellationToken);

    Task<CharacterProfileResponse> CreateAsync(
        CreateCharacterProfileRequest request,
        CancellationToken cancellationToken);

    Task<CharacterProfileResponse?> UpdateAsync(
        Guid characterProfileId,
        UpdateCharacterProfileRequest request,
        CancellationToken cancellationToken);

    Task<CharacterProfileResponse?> SetAvatarAsync(
        Guid characterProfileId,
        Stream avatar,
        string fileName,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid characterProfileId, CancellationToken cancellationToken);

    Task<CharacterProfileAvatar?> GetAvatarAsync(Guid characterProfileId, CancellationToken cancellationToken);

    Task<CharacterProfileResponse?> SetActiveAsync(Guid characterProfileId, bool isActive, CancellationToken cancellationToken);

    Task<GeneratedCharacterProfileAssistResponse> GenerateAssistAsync(
        GenerateCharacterProfileAssistRequest request,
        CancellationToken cancellationToken);
}

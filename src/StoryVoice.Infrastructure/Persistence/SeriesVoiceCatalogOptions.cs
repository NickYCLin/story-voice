namespace StoryVoice.Infrastructure.Persistence;

public sealed class SeriesVoiceCatalogOptions
{
    public const string SectionName = "SeriesVoiceCatalog";

    public List<SeriesVoiceCatalogEntry> Voices { get; set; } = [];

    // Legacy configuration type retained for compatibility. New voice options come from
    // owner-scoped Character Management profiles, never a static provider catalog.
    public static List<SeriesVoiceCatalogEntry> CreateDefaultVoices() => [];

}

public sealed class SeriesVoiceCatalogEntry
{
    public string Provider { get; set; } = string.Empty;
    public string Voice { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Locale { get; set; } = string.Empty;
    public string? Gender { get; set; }
    public int? MinimumAge { get; set; }
    public int? MaximumAge { get; set; }
    public List<string> CharacterTags { get; set; } = [];

    public bool HasValidCastingMetadata() =>
        Gender is null or "male" or "female" or "neutral"
        && MinimumAge is null or >= 0 and <= 150
        && MaximumAge is null or >= 0 and <= 150
        && (MinimumAge is null || MaximumAge is null || MinimumAge <= MaximumAge)
        && CharacterTags is { Count: <= 16 }
        && CharacterTags.All(tag => !string.IsNullOrWhiteSpace(tag) && tag.Length <= 40 && !tag.Any(char.IsControl))
        && CharacterTags.Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == CharacterTags.Count;
}

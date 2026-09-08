using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

public sealed class SeriesVoiceCatalogOptions
{
    public const string SectionName = "SeriesVoiceCatalog";

    public List<SeriesVoiceCatalogEntry> Voices { get; set; } = [];

    public static List<SeriesVoiceCatalogEntry> CreateDefaultVoices() =>
    [
        new()
        {
            Provider = "edge",
            Voice = "zh-TW-HsiaoChenNeural",
            DisplayName = "曉臻（女聲）",
            Gender = "female",
            Locale = "zh-TW"
        },
        new()
        {
            Provider = "edge",
            Voice = "zh-TW-YunJheNeural",
            DisplayName = "雲哲（男聲）",
            Gender = "male",
            Locale = "zh-TW"
        },
        new()
        {
            Provider = "edge",
            Voice = "zh-CN-YunxiNeural",
            DisplayName = "雲希（男聲）",
            Gender = "male",
            Locale = "zh-CN"
        },
        new()
        {
            Provider = "edge",
            Voice = "zh-CN-YunjianNeural",
            DisplayName = "雲健（男聲）",
            Gender = "male",
            Locale = "zh-CN"
        },
        new()
        {
            Provider = "edge",
            Voice = "zh-CN-XiaoxiaoNeural",
            DisplayName = "曉曉（女聲）",
            Gender = "female",
            Locale = "zh-CN"
        },
        new()
        {
            Provider = "edge",
            Voice = "zh-CN-XiaoyiNeural",
            DisplayName = "曉伊（女聲）",
            Gender = "female",
            Locale = "zh-CN"
        },
        new()
        {
            Provider = "edge",
            Voice = "zh-TW-HsiaoYuNeural",
            DisplayName = "曉雨（女聲）",
            Gender = "female",
            Locale = "zh-TW"
        },
        new()
        {
            Provider = CharacterVoiceProviders.BlueMagpie,
            Voice = "female_voice",
            DisplayName = "BlueMagpie 內建女聲（私人自架）",
            Gender = "female",
            Locale = "zh-TW"
        },
        new()
        {
            Provider = CharacterVoiceProviders.BlueMagpie,
            Voice = "hung_yi_lee",
            DisplayName = "BlueMagpie 內建男聲（私人自架）",
            Gender = "male",
            Locale = "zh-TW"
        },
        new()
        {
            // VoAI's public VoiceAPI documentation uses this exact model, speaker, and style in
            // its synthesis examples. Keep all three pinned in the provider-scoped reference.
            Provider = CharacterVoiceProviders.VoAi,
            Voice = "v1:Neo:佑希:預設",
            DisplayName = "VoAI 佑希（Neo／預設）",
            Locale = "zh-TW"
        },
        new()
        {
            // Characters resolve to linked Clone profiles at synthesis time. Narrator and
            // explicitly confirmed narrator-fallback turns still need a real Edge voice because
            // the narrator itself has no CharacterVoiceProfile.
            Provider = "3wa-voxcpm2",
            Voice = ThreeWaSynthesisCapabilities.NarratorFallbackVoice,
            DisplayName = "3wa 角色克隆（旁白：雲哲）",
            Locale = "zh-TW"
        }
    ];
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

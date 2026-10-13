namespace StoryVoice.Infrastructure.Narrations;

public sealed class MultiCharacterNarrationOptions
{
    public const string SectionName = "MultiCharacterNarration";

    public string ProviderVersion { get; set; } = "edge-tts-7.2.8";
    public int ChapterPauseMs { get; set; } = 700;
    public string CompositionVersion { get; set; } = "edge-tts-multi-voice-v1";
    public string FfmpegProfile { get; set; } = "concat-demuxer-mp3-v1";

    public Dictionary<string, string> ProviderVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edge"] = "edge-tts-7.2.8",
        ["3wa-voxcpm2"] = "3wa-voxcpm2-api-v1",
        ["voai"] = "voai-voice-api-v1",
        ["bluemagpie"] = BlueMagpieOptions.PinnedProviderVersion,
        ["local-clone"] = LocalClonePreviewOptions.FormalProviderVersion,
    };

    public Dictionary<string, string> CompositionVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edge"] = "edge-tts-multi-voice-v1",
        ["3wa-voxcpm2"] = "3wa-voxcpm2-turn-concat-v2",
        ["voai"] = "voai-speech-turn-concat-v1",
        ["bluemagpie"] = "bluemagpie-pcm16-concat-v1",
        ["local-clone"] = "local-clone-complete-turns-v2",
    };

    public Dictionary<string, string> FfmpegProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edge"] = "concat-demuxer-mp3-v1",
        ["3wa-voxcpm2"] = "mixed-audio-to-mp3-concat-v1",
        ["voai"] = "wav-32khz-to-mp3-concat-v1",
        ["bluemagpie"] = "wav-48khz-mono-to-mp3-concat-v1",
        ["local-clone"] = "mp3-44100-mono-concat-v1",
    };

    public string ResolveProviderVersion(string provider) =>
        Resolve(ProviderVersions, provider, ProviderVersion, "provider version");

    public string ResolveCompositionVersion(string provider) =>
        Resolve(CompositionVersions, provider, CompositionVersion, "composition version");

    public string ResolveFfmpegProfile(string provider) =>
        Resolve(FfmpegProfiles, provider, FfmpegProfile, "ffmpeg profile");

    private static string Resolve(
        IReadOnlyDictionary<string, string>? values,
        string provider,
        string legacyFallback,
        string settingName)
    {
        var normalizedProvider = provider?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedProvider))
        {
            throw new InvalidOperationException($"Multi-character narration {settingName} provider is missing.");
        }

        var configured = values?
            .FirstOrDefault(entry => string.Equals(
                entry.Key?.Trim(),
                normalizedProvider,
                StringComparison.OrdinalIgnoreCase))
            .Value;
        var resolved = string.IsNullOrWhiteSpace(configured) ? legacyFallback : configured;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                $"Multi-character narration {settingName} is missing for provider '{normalizedProvider}'.");
        }

        return resolved.Trim();
    }
}

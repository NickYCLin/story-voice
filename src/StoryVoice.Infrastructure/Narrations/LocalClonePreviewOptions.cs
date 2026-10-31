namespace StoryVoice.Infrastructure.Narrations;

public sealed class LocalClonePreviewOptions
{
    public const string SectionName = "LocalClonePreview";
    public const string PinnedGatewayBaseUrl = "http://local-clone-gateway:8082/";
    public const string PinnedCosyVoiceSourceRevision = "074ca6dc9e80a2f424f1f74b48bdd7d3fea531cc";
    public const string PinnedModelId = "FunAudioLLM/Fun-CosyVoice3-0.5B-2512";
    public const string PinnedModelRevision = "29e01c4e8d000f4bcd70751be16fa94bf3d85a18";
    public const int MaximumReferenceAudioBytes = 10 * 1024 * 1024;
    public const int MaximumTranscriptBytes = 32 * 1024;

    public const string FormalProviderVersion = "local-clone-managed-voices-v3";
    public bool FormalNarrationEnabled { get; set; }
    public int RetryDelaySeconds { get; set; } = 20;

    public bool Enabled { get; set; }

    public string GatewayBaseUrl { get; set; } = PinnedGatewayBaseUrl;

    public string InternalToken { get; set; } = string.Empty;

    public string AssetRootPath { get; set; } = "storage/local-clone-preview-assets";

    public int ConnectTimeoutSeconds { get; set; } = 10;

    public int RequestTimeoutSeconds { get; set; } = 240;

    public int MaximumResponseBytes { get; set; } = 16 * 1024 * 1024;

    public Dictionary<string, LocalClonePreviewAssetOptions> AllowedProfiles { get; set; } = [];
}

public sealed class LocalClonePreviewAssetOptions
{
    public string Label { get; set; } = string.Empty;

    public string ReferenceAudioRelativePath { get; set; } = string.Empty;

    public string TranscriptRelativePath { get; set; } = string.Empty;

    public string ExpectedReferenceAudioSha256 { get; set; } = string.Empty;

    public string ExpectedTranscriptSha256 { get; set; } = string.Empty;
}

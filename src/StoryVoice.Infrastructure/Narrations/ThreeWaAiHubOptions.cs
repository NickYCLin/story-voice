namespace StoryVoice.Infrastructure.Narrations;

public sealed class ThreeWaAiHubOptions
{
    public const string SectionName = "ThreeWaAiHub";

    public string BaseUrl { get; set; } = "https://3wa.tw/3waAIHub/";
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Optional non-secret identifier for the configured token version. When omitted StoryVoice
    /// stores a short SHA-256 fingerprint, never the token itself, in durable clone operations.
    /// </summary>
    public string CredentialKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Upper bound for a single HTTP round-trip to 3wa (headers phase; body reads are bounded by
    /// the caller's token). Prevents a stalled station from pinning a request forever.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 300;

    /// <summary>Maximum accepted JSON body for submit, status, and result responses.</summary>
    public int MaximumJsonResponseBytes { get; set; } = 64 * 1024;

    /// <summary>Maximum audio artifact downloaded by a single synthesis request.</summary>
    public int MaximumAudioResponseBytes { get; set; } = 20 * 1024 * 1024;
}

/// <summary>
/// Code-pinned 3wa capabilities. Design must not be enabled by environment configuration: the
/// official manifest currently does not forward <c>voice_prompt</c> to VoxCPM2, and the production
/// account currently has no configured managed voice presets. Treating prompt text as a character
/// identity would therefore silently collapse every designed role onto the same voice.
/// </summary>
public static class ThreeWaSynthesisCapabilities
{
    public static bool SupportsVoiceDesign => false;
    /// <summary>
    /// A receipt uploaded by an owner is an auditable self-attestation, not a trusted issuer
    /// signature. Keep full-book synthesis code-pinned off until StoryVoice has an independent
    /// approval/signature workflow. Private, owner-scoped preview remains available.
    /// </summary>
    public static bool SupportsTrustedCloneFormalNarration => false;
    public const string DesignVoiceUnavailableCode = "3wa_design_voice_unsupported";
    public const string DesignVoiceUnavailableMessage =
        "3wa 目前不會把 voice_prompt 傳給 VoxCPM2，文字設計聲線無法產生可區分的角色聲音；請改用已授權且已完成的克隆聲線。";
    public const string CloneVoiceUnavailableCode = "3wa_clone_voice_unavailable";
    public const string CloneVoiceUnavailableMessage =
        "3wa 角色正式配音必須連結一組已完成的克隆基礎聲線。";
    public const string CloneFormalNarrationUnavailableCode =
        "3wa_clone_formal_authorization_unverified";
    public const string CloneFormalNarrationUnavailableMessage =
        "3wa 克隆目前只開放私人試音；上傳的授權 receipt 是使用者聲明，尚未經 StoryVoice 的可信簽章或人工核准，因此不能用於正式有聲書。";
    public const string NarratorFallbackVoice = "zh-TW-YunJheNeural";
    public const string LegacyNarratorVoiceUnavailableCode = "3wa_legacy_narrator_voice_unavailable";
    public const string LegacyNarratorVoiceUnavailableMessage =
        "舊版 3wa 旁白聲線只保存了 custom 佔位值，無法安全合成；請重新建立系列聲線版本。";
}

namespace StoryVoice.Infrastructure.ExternalVoices;

public sealed class ExternalVoiceApiOptions
{
    public const string SectionName = "ExternalVoiceApi";
    public const int DefaultMaximumRequestBytes = 4_096;
    public const int DefaultMaximumTextCharacters = 200;
    public const int DefaultMaximumTextUtf8Bytes = 2_048;
    public const int DefaultMaximumResponseBytes = 3 * 1024 * 1024;
    public const int DefaultRequestsPerMinute = 3;
    public const int DefaultPreAuthenticationRequestsPerMinute = 60;
    public const int DefaultPreAuthenticationGlobalRequestsPerMinute = 600;
    public const int DefaultIdempotencyCacheEntries = 32;
    public const int DefaultIdempotencyTtlMinutes = 10;
    public const int DefaultUsageLedgerQueueCapacity = 1_024;
    public const int MaximumUsageLedgerQueueCapacity = 10_000;
    public bool Enabled { get; set; }

    public int MaximumRequestBytes { get; set; } = DefaultMaximumRequestBytes;

    public int MaximumTextCharacters { get; set; } = DefaultMaximumTextCharacters;

    public int MaximumTextUtf8Bytes { get; set; } = DefaultMaximumTextUtf8Bytes;

    public int MaximumResponseBytes { get; set; } = DefaultMaximumResponseBytes;

    public int RequestsPerMinute { get; set; } = DefaultRequestsPerMinute;

    public int PreAuthenticationRequestsPerMinute { get; set; } =
        DefaultPreAuthenticationRequestsPerMinute;

    public int PreAuthenticationGlobalRequestsPerMinute { get; set; } =
        DefaultPreAuthenticationGlobalRequestsPerMinute;

    public int IdempotencyCacheEntries { get; set; } = DefaultIdempotencyCacheEntries;

    public int IdempotencyTtlMinutes { get; set; } = DefaultIdempotencyTtlMinutes;

    public int UsageLedgerQueueCapacity { get; set; } = DefaultUsageLedgerQueueCapacity;

    public Dictionary<string, ExternalVoiceConsumerOptions> Consumers { get; set; } = [];
}

public sealed class ExternalVoiceConsumerOptions
{
    public required string AccessTier { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    public string ConsumerFamilyId { get; set; } = string.Empty;

    public string TerritoryCountryCode { get; set; } = string.Empty;

    public Guid OwnerId { get; set; }

    public string TokenSha256 { get; set; } = string.Empty;

    public DateTimeOffset EffectiveAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public Dictionary<string, ExternalVoiceGrantOptions> AllowedVoices { get; set; } = [];
}

public static class ExternalVoiceAccessTiers
{
    public const string PrivateDevelopment = "private-development";
    public const string SubscriptionCommercial = "subscription-commercial";
    public const string PrivateDevelopmentTokenPrefix = "svd1.";
    public const string SubscriptionCommercialTokenPrefix = "svv1.";

    public static bool IsSupported(string value) =>
        string.Equals(value, PrivateDevelopment, StringComparison.Ordinal)
        || string.Equals(value, SubscriptionCommercial, StringComparison.Ordinal);
}

public sealed class ExternalVoiceGrantOptions
{
    public string AuthorizationEvidenceRelativePath { get; set; } = string.Empty;

    public string AuthorizationEvidenceSha256 { get; set; } = string.Empty;

    public DateTimeOffset? RevokedAtUtc { get; set; }
}

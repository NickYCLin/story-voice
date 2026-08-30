using System.Globalization;
using Microsoft.Extensions.Options;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.VoiceCatalog;

namespace StoryVoice.Infrastructure.ExternalVoices;

internal sealed class ExternalVoiceApiOptionsValidator(
    IOptions<LocalClonePreviewOptions> localCloneOptions,
    IOptions<VoiceCatalogOptions> voiceCatalogOptions)
    : IValidateOptions<ExternalVoiceApiOptions>
{
    public ValidateOptionsResult Validate(string? name, ExternalVoiceApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        ValidateLimits(options, failures);
        ValidateConsumers(options, failures);

        if (options.Enabled)
        {
            var local = localCloneOptions.Value;
            var catalog = voiceCatalogOptions.Value;
            var hasCommercialConsumer = options.Consumers?.Values.Any(consumer =>
                consumer is not null
                && string.Equals(
                    consumer.AccessTier,
                    ExternalVoiceAccessTiers.SubscriptionCommercial,
                    StringComparison.Ordinal)) == true;
            if (!IsValidLocalCloneInternalToken(local.InternalToken))
            {
                failures.Add("Enabled external voice API requires a valid local clone internal token.");
            }

            if (!IsValidPrivateAssetRoot(local.AssetRootPath))
            {
                failures.Add("Enabled external voice API requires a non-root private asset directory.");
            }

            if (!IsValidLocalCloneAllowlist(local.AllowedProfiles))
            {
                failures.Add("Enabled external voice API requires a valid local clone profile allowlist.");
            }

            if (hasCommercialConsumer && !IsValidPrivateAssetRoot(catalog.AssetRootPath))
            {
                failures.Add(
                    "Commercial external voice API access requires a non-root synthetic voice asset directory.");
            }

            if (hasCommercialConsumer && catalog.Entries is not { Count: > 0 })
            {
                failures.Add(
                    "Commercial external voice API access requires at least one configured synthetic voice entry.");
            }

            if (options.Consumers is null || options.Consumers.Count == 0)
            {
                failures.Add("Enabled external voice API requires at least one consumer.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateLimits(
        ExternalVoiceApiOptions options,
        ICollection<string> failures)
    {
        if (options.MaximumRequestBytes is < 256 or > ExternalVoiceApiOptions.DefaultMaximumRequestBytes)
        {
            failures.Add("External voice request limit must be between 256 bytes and 4096 bytes.");
        }

        if (options.MaximumTextCharacters is < 1 or > ExternalVoiceApiOptions.DefaultMaximumTextCharacters)
        {
            failures.Add("External voice text character limit must be between 1 and 200.");
        }

        if (options.MaximumTextUtf8Bytes is < 1 or > ExternalVoiceApiOptions.DefaultMaximumTextUtf8Bytes)
        {
            failures.Add("External voice UTF-8 text limit must be between 1 byte and 2048 bytes.");
        }

        if (options.MaximumResponseBytes is < 64 * 1024 or > ExternalVoiceApiOptions.DefaultMaximumResponseBytes)
        {
            failures.Add("External voice response limit must be between 64 KiB and 3 MiB.");
        }

        if (options.RequestsPerMinute is < 1 or > ExternalVoiceApiOptions.DefaultRequestsPerMinute)
        {
            failures.Add("External voice rate limit must be between 1 and 3 requests per minute.");
        }

        if (options.PreAuthenticationRequestsPerMinute is < 1 or > 600)
        {
            failures.Add(
                "External voice pre-authentication source limit must be between 1 and 600 requests per minute.");
        }

        if (options.PreAuthenticationGlobalRequestsPerMinute
                < options.PreAuthenticationRequestsPerMinute
            || options.PreAuthenticationGlobalRequestsPerMinute > 6_000)
        {
            failures.Add(
                "External voice pre-authentication global limit must be at least the source limit and no more than 6000 requests per minute.");
        }

        if (options.IdempotencyCacheEntries is < 1 or > ExternalVoiceApiOptions.DefaultIdempotencyCacheEntries)
        {
            failures.Add("External voice idempotency cache must contain between 1 and 32 entries.");
        }

        if (options.IdempotencyTtlMinutes is < 1 or > ExternalVoiceApiOptions.DefaultIdempotencyTtlMinutes)
        {
            failures.Add("External voice idempotency TTL must be between 1 and 10 minutes.");
        }

        if (options.UsageLedgerQueueCapacity is < 1
            or > ExternalVoiceApiOptions.MaximumUsageLedgerQueueCapacity)
        {
            failures.Add("External voice usage ledger queue must contain between 1 and 10000 entries.");
        }
    }

    private static void ValidateConsumers(
        ExternalVoiceApiOptions options,
        ICollection<string> failures)
    {
        if (options.Consumers is null)
        {
            failures.Add("External voice consumers collection is required.");
            return;
        }

        var projectReferences = new Dictionary<(Guid OwnerId, string Reference), string>();
        foreach (var (consumerKeyId, consumer) in options.Consumers)
        {
            if (!IsCanonicalConsumerKeyId(consumerKeyId))
            {
                failures.Add($"External voice consumer key id '{consumerKeyId}' is not canonical.");
            }

            if (consumer is null)
            {
                failures.Add($"External voice consumer '{consumerKeyId}' is required.");
                continue;
            }

            if (consumer.OwnerId != Guid.Empty
                && !string.IsNullOrEmpty(consumer.ProjectId))
            {
                AddProjectReference(
                    projectReferences,
                    consumer.OwnerId,
                    consumerKeyId,
                    consumerKeyId,
                    failures);
                AddProjectReference(
                    projectReferences,
                    consumer.OwnerId,
                    consumer.ProjectId,
                    consumerKeyId,
                    failures);
            }

            var validCommonFields = ExternalVoiceAccessTiers.IsSupported(consumer.AccessTier)
                && IsPrintableDisplayName(consumer.DisplayName)
                && VoiceCatalogOptionsValidator.IsCanonicalGrantIdentifier(consumer.ProjectId)
                && consumer.OwnerId != Guid.Empty
                && IsCanonicalSha256(consumer.TokenSha256)
                && IsValidUtcWindow(consumer.EffectiveAtUtc, consumer.ExpiresAtUtc);
            if (!validCommonFields)
            {
                failures.Add($"External voice consumer '{consumerKeyId}' is invalid.");
            }

            var isPrivateDevelopment = string.Equals(
                consumer.AccessTier,
                ExternalVoiceAccessTiers.PrivateDevelopment,
                StringComparison.Ordinal);
            var isSubscriptionCommercial = string.Equals(
                consumer.AccessTier,
                ExternalVoiceAccessTiers.SubscriptionCommercial,
                StringComparison.Ordinal);
            if (isPrivateDevelopment
                && (!string.IsNullOrEmpty(consumer.ConsumerFamilyId)
                    || !string.IsNullOrEmpty(consumer.TerritoryCountryCode)
                    || consumer.ExpiresAtUtc - consumer.EffectiveAtUtc
                        > ExternalVoiceDevelopmentGrantValidator.MaximumGrantDuration))
            {
                failures.Add(
                    $"Private development consumer '{consumerKeyId}' must omit commercial scope and expire within 30 days.");
            }

            if (isSubscriptionCommercial
                && (!VoiceCatalogOptionsValidator.IsCanonicalGrantIdentifier(
                        consumer.ConsumerFamilyId)
                    || !VoiceCatalogOptionsValidator.IsCountryCode(
                        consumer.TerritoryCountryCode)))
            {
                failures.Add(
                    $"Commercial external voice consumer '{consumerKeyId}' has invalid family or territory scope.");
            }

            if (consumer.AllowedVoices is null || consumer.AllowedVoices.Count == 0)
            {
                failures.Add($"External voice consumer '{consumerKeyId}' requires at least one voice grant.");
                continue;
            }

            if (isPrivateDevelopment && consumer.AllowedVoices.Count != 1)
            {
                failures.Add(
                    $"Private development consumer '{consumerKeyId}' requires exactly one short-term voice grant.");
            }

            foreach (var (voice, grant) in consumer.AllowedVoices)
            {
                if (!IsCanonicalVoiceAlias(voice) || grant is null || !IsValidGrant(grant))
                {
                    failures.Add($"External voice grant '{consumerKeyId}/{voice}' is invalid.");
                    continue;
                }

            }
        }
    }

    private static void AddProjectReference(
        IDictionary<(Guid OwnerId, string Reference), string> projectReferences,
        Guid ownerId,
        string projectReference,
        string consumerKeyId,
        ICollection<string> failures)
    {
        var key = (ownerId, projectReference);
        if (projectReferences.TryGetValue(key, out var existingConsumerKeyId))
        {
            if (!string.Equals(existingConsumerKeyId, consumerKeyId, StringComparison.Ordinal))
            {
                failures.Add(
                    $"External voice consumer '{consumerKeyId}' duplicates owner-scoped project reference '{projectReference}'.");
            }

            return;
        }

        projectReferences.Add(key, consumerKeyId);
    }

    internal static bool IsCanonicalConsumerKeyId(string value) =>
        value is { Length: >= 8 and <= 64 }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '_');

    internal static bool IsCanonicalVoiceAlias(string value) =>
        value is { Length: >= 1 and <= 64 }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-');

    internal static bool IsCanonicalSha256(string value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsValidRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || Path.IsPathRooted(value))
        {
            return false;
        }

        return value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .All(segment => segment is not ("" or "." or ".."));
    }

    private static bool IsValidGrant(ExternalVoiceGrantOptions grant) =>
        IsValidRelativePath(grant.AuthorizationEvidenceRelativePath)
        && string.Equals(
            Path.GetExtension(grant.AuthorizationEvidenceRelativePath),
            ".json",
            StringComparison.OrdinalIgnoreCase)
        && IsCanonicalSha256(grant.AuthorizationEvidenceSha256)
        && (grant.RevokedAtUtc is null
            || grant.RevokedAtUtc != default
                && grant.RevokedAtUtc.Value.Offset == TimeSpan.Zero);

    private static bool IsValidUtcWindow(DateTimeOffset effectiveAtUtc, DateTimeOffset expiresAtUtc) =>
        effectiveAtUtc != default
        && expiresAtUtc != default
        && effectiveAtUtc.Offset == TimeSpan.Zero
        && expiresAtUtc.Offset == TimeSpan.Zero
        && expiresAtUtc > effectiveAtUtc;

    private static bool IsPrintableDisplayName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 120
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && value.All(character => !char.IsControl(character)
            && char.GetUnicodeCategory(character) != UnicodeCategory.Format);

    private static bool IsValidLocalCloneInternalToken(string value) =>
        value is { Length: >= 32 and <= 512 }
        && value.All(character => character is >= '!' and <= '~');

    private static bool IsValidPrivateAssetRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            var pathRoot = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(pathRoot)
                && !string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsValidLocalCloneAllowlist(
        Dictionary<string, LocalClonePreviewAssetOptions>? allowedProfiles) =>
        allowedProfiles is { Count: > 0 }
        && allowedProfiles.All(candidate =>
            Guid.TryParseExact(candidate.Key, "D", out var profileId)
            && string.Equals(candidate.Key, profileId.ToString("D"), StringComparison.Ordinal)
            && candidate.Value is not null
            && IsPrintableDisplayName(candidate.Value.Label)
            && IsValidRelativePath(candidate.Value.ReferenceAudioRelativePath)
            && string.Equals(
                Path.GetExtension(candidate.Value.ReferenceAudioRelativePath),
                ".wav",
                StringComparison.OrdinalIgnoreCase)
            && IsValidRelativePath(candidate.Value.TranscriptRelativePath)
            && string.Equals(
                Path.GetExtension(candidate.Value.TranscriptRelativePath),
                ".txt",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                candidate.Value.ReferenceAudioRelativePath,
                candidate.Value.TranscriptRelativePath,
                StringComparison.Ordinal)
            && IsCanonicalSha256(candidate.Value.ExpectedReferenceAudioSha256)
            && IsCanonicalSha256(candidate.Value.ExpectedTranscriptSha256));
}

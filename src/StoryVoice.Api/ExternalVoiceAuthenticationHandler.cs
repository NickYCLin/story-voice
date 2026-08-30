using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using StoryVoice.Infrastructure.ExternalVoices;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Api;

public static class ExternalVoiceAuthenticationDefaults
{
    public const string Scheme = "StoryVoiceExternalVoice";
    public const string ConsumerKeyIdClaim = "consumer_key_id";
    public const string CredentialKeyIdClaim = "credential_key_id";
    public const string ExternalOwnerIdClaim = "external_owner_id";
    public const string ScopeClaim = "storyvoice_scope";
    public const string SynthesisScope = "voice:synthesize";
}

public sealed class ExternalVoiceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ExternalVoiceApiOptions> apiOptions,
    StoryVoiceDbContext dbContext,
    ExternalVoiceCredentialUsageUpdater credentialUsageUpdater,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    private const string DummyTokenSha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryReadBearerToken(out var rawToken, out var keyId, out var tokenAccessTier))
        {
            return AuthenticateResult.NoResult();
        }

        var options = apiOptions.Value;
        var now = timeProvider.GetUtcNow();
        options.Consumers.TryGetValue(keyId, out var consumer);
        var hashMatches = MatchesConfiguredHash(
            rawToken,
            consumer?.TokenSha256 ?? DummyTokenSha256);
        if (hashMatches && IsConsumerAvailable(options, consumer, tokenAccessTier, now))
        {
            return CreateSuccess(keyId, keyId, consumer!.OwnerId);
        }

        var credential = await dbContext.ExternalVoiceCredentials.SingleOrDefaultAsync(
            candidate => candidate.KeyId == keyId,
            Context.RequestAborted);
        var managedHashMatches = MatchesConfiguredHash(
            rawToken,
            credential?.TokenSha256 ?? DummyTokenSha256);
        ExternalVoiceConsumerOptions? managedConsumer = null;
        if (credential is null
            || !managedHashMatches
            || credential.ExpiresAtUtc <= now
            || credential.RevokedAtUtc is { } revokedAt && revokedAt <= now
            || !options.Consumers.TryGetValue(credential.ConsumerKeyId, out managedConsumer)
            || managedConsumer.OwnerId != credential.OwnerId
            || !IsConsumerAvailable(options, managedConsumer, tokenAccessTier, now))
        {
            return AuthenticateResult.Fail("External voice API key is invalid.");
        }

        await credentialUsageUpdater.RecordUseAsync(
            credential,
            now,
            Context.RequestAborted);
        return CreateSuccess(credential.ConsumerKeyId, credential.KeyId, credential.OwnerId);
    }

    private static AuthenticateResult CreateSuccess(
        string consumerKeyId,
        string credentialKeyId,
        Guid ownerId)
    {
        var claims = new[]
        {
            new Claim(ExternalVoiceAuthenticationDefaults.ConsumerKeyIdClaim, consumerKeyId),
            new Claim(ExternalVoiceAuthenticationDefaults.CredentialKeyIdClaim, credentialKeyId),
            new Claim(
                ExternalVoiceAuthenticationDefaults.ExternalOwnerIdClaim,
                ownerId.ToString("D")),
            new Claim(
                ExternalVoiceAuthenticationDefaults.ScopeClaim,
                ExternalVoiceAuthenticationDefaults.SynthesisScope),
        };
        var identity = new ClaimsIdentity(claims, ExternalVoiceAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, ExternalVoiceAuthenticationDefaults.Scheme));
    }

    private static bool IsConsumerAvailable(
        ExternalVoiceApiOptions options,
        ExternalVoiceConsumerOptions? consumer,
        string tokenAccessTier,
        DateTimeOffset now) =>
        options.Enabled
        && consumer is not null
        && string.Equals(consumer.AccessTier, tokenAccessTier, StringComparison.Ordinal)
        && consumer.OwnerId != Guid.Empty
        && consumer.EffectiveAtUtc <= now
        && consumer.ExpiresAtUtc > now;

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        await ExternalVoiceEndpoints.WriteProblemAsync(
            Context,
            StatusCodes.Status401Unauthorized,
            "Invalid API key",
            "A valid external voice API key is required.",
            "invalid_api_key",
            Context.RequestAborted);
    }

    private bool TryReadBearerToken(
        out string rawToken,
        out string keyId,
        out string tokenAccessTier)
    {
        rawToken = string.Empty;
        keyId = string.Empty;
        tokenAccessTier = string.Empty;
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out var values)
            || values.Count != 1)
        {
            return false;
        }

        const string bearerPrefix = "Bearer ";
        var authorization = values[0];
        if (authorization is null
            || !authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = authorization[bearerPrefix.Length..];
        if (!TryParseCanonicalToken(candidate, out keyId, out tokenAccessTier))
        {
            return false;
        }

        rawToken = candidate;
        return true;
    }

    private static bool TryParseCanonicalToken(
        string token,
        out string keyId,
        out string accessTier)
    {
        keyId = string.Empty;
        accessTier = string.Empty;
        string prefix;
        if (token.StartsWith(
                ExternalVoiceAccessTiers.PrivateDevelopmentTokenPrefix,
                StringComparison.Ordinal))
        {
            prefix = ExternalVoiceAccessTiers.PrivateDevelopmentTokenPrefix;
            accessTier = ExternalVoiceAccessTiers.PrivateDevelopment;
        }
        else if (token.StartsWith(
                     ExternalVoiceAccessTiers.SubscriptionCommercialTokenPrefix,
                     StringComparison.Ordinal))
        {
            prefix = ExternalVoiceAccessTiers.SubscriptionCommercialTokenPrefix;
            accessTier = ExternalVoiceAccessTiers.SubscriptionCommercial;
        }
        else
        {
            return false;
        }

        const int minimumTokenLength = 5 + 8 + 1 + 43;
        const int maximumTokenLength = 5 + 64 + 1 + 43;
        if (token is not { Length: >= minimumTokenLength and <= maximumTokenLength })
        {
            accessTier = string.Empty;
            return false;
        }

        var keyEnd = token.IndexOf('.', prefix.Length);
        if (keyEnd < 0)
        {
            accessTier = string.Empty;
            return false;
        }

        var candidateKeyId = token[prefix.Length..keyEnd];
        var secret = token[(keyEnd + 1)..];
        if (candidateKeyId is not { Length: >= 8 and <= 64 }
            || candidateKeyId[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9')
            || candidateKeyId[1..].Any(character => character is not (
                >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-'))
            || secret.Length != 43
            || secret.Any(character => character is not (
                >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-')))
        {
            accessTier = string.Empty;
            return false;
        }

        keyId = candidateKeyId;
        return true;
    }

    private static bool MatchesConfiguredHash(string rawToken, string configuredHash)
    {
        if (configuredHash is not { Length: 64 }
            || configuredHash.Any(character => character is not (
                >= '0' and <= '9'
                or >= 'a' and <= 'f')))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[32];
        for (var index = 0; index < expected.Length; index++)
        {
            expected[index] = (byte)((HexValue(configuredHash[index * 2]) << 4)
                | HexValue(configuredHash[(index * 2) + 1]));
        }

        var tokenBytes = Encoding.UTF8.GetBytes(rawToken);
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(tokenBytes, actual);
        CryptographicOperations.ZeroMemory(tokenBytes);
        var matches = CryptographicOperations.FixedTimeEquals(actual, expected);
        CryptographicOperations.ZeroMemory(actual);
        CryptographicOperations.ZeroMemory(expected);
        return matches;
    }

    private static int HexValue(char character) =>
        character <= '9' ? character - '0' : character - 'a' + 10;
}

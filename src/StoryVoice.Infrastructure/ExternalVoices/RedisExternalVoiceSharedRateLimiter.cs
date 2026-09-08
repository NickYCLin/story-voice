using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StoryVoice.Application.ExternalVoices;

namespace StoryVoice.Infrastructure.ExternalVoices;

internal sealed class RedisExternalVoiceSharedRateLimiter(
    IOptions<ExternalVoiceApiOptions> options,
    Func<IConnectionMultiplexer> connectionFactory) : IExternalVoiceSharedRateLimiter
{
    private const string KeyPrefix = "storyvoice:external-voice:rate-limit:v1:";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private const string AcquireScript = """
        local counts = {}
        local retry = 0
        for i, key in ipairs(KEYS) do
            local value = redis.call('GET', key)
            local count = 0
            local ttl = 60000
            if value then
                count = tonumber(value)
                ttl = redis.call('PTTL', key)
                if not count or count < 1 or count ~= math.floor(count) or ttl <= 0 or ttl > 60000 then
                    return {-1, 0}
                end
            end
            counts[i] = count
            if count >= tonumber(ARGV[i]) then
                retry = math.max(retry, math.ceil(ttl / 1000))
            end
        end
        if retry > 0 then return {0, retry} end
        for i, key in ipairs(KEYS) do
            if counts[i] == 0 then
                redis.call('SET', key, 1, 'PX', 60000)
            else
                redis.call('INCR', key)
            end
        end
        return {1, 0}
        """;

    public async Task EnsureAllowedAsync(string consumerKeyId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentOptions = options.Value;
        if (!currentOptions.SharedRateLimitEnabled)
        {
            return;
        }

        if (!ExternalVoiceApiOptionsValidator.IsCanonicalConsumerKeyId(consumerKeyId)
            || !currentOptions.Consumers.ContainsKey(consumerKeyId))
        {
            throw new ExternalVoiceSynthesisException(ExternalVoiceSynthesisFailureKind.VoiceNotAvailable);
        }

        await EnsureAsync([CreateKey(consumerKeyId)], [currentOptions.RequestsPerMinute], cancellationToken);
    }

    public async Task EnsurePreAuthenticationAllowedAsync(IPAddress? sourceAddress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentOptions = options.Value;
        if (!currentOptions.SharedPreAuthenticationRateLimitEnabled) return;
        if (currentOptions.SharedPreAuthenticationHashKey is not { Length: 64 }
            || !currentOptions.SharedPreAuthenticationHashKey.All(Uri.IsHexDigit)) throw Unavailable();

        var bucket = ExternalVoiceSourceBuckets.Resolve(sourceAddress,
            Convert.FromHexString(currentOptions.SharedPreAuthenticationHashKey));
        await EnsureAsync(
            [PreAuthenticationGlobalKey, CreatePreAuthenticationSourceKey(bucket)],
            [currentOptions.PreAuthenticationGlobalRequestsPerMinute, currentOptions.PreAuthenticationRequestsPerMinute],
            cancellationToken);
    }

    private async Task EnsureAsync(RedisKey[] keys, RedisValue[] limits, CancellationToken cancellationToken)
    {
        try
        {
            var database = connectionFactory().GetDatabase();
            var result = await database.ScriptEvaluateAsync(
                    AcquireScript,
                    keys,
                    limits,
                    CommandFlags.DemandMaster)
                .WaitAsync(CommandTimeout, cancellationToken);
            var values = (RedisResult[]?)result;
            if (values is not { Length: 2 })
            {
                throw Unavailable();
            }

            var allowed = (long)values[0];
            var retryAfter = (long)values[1];
            if (allowed == 0 && retryAfter is >= 1 and <= 60)
            {
                throw new ExternalVoiceSynthesisException(
                    ExternalVoiceSynthesisFailureKind.RateLimited,
                    (int)retryAfter);
            }

            if (allowed != 1 || retryAfter != 0)
            {
                throw Unavailable();
            }
        }
        catch (Exception exception) when (exception is RedisException
            or TimeoutException
            or InvalidOperationException
            or InvalidCastException
            or FormatException
            or OverflowException)
        {
            // Connection diagnostics may contain private hosts. Do not include them
            // in the public failure or fall back to a separate process-local budget.
            throw Unavailable();
        }
    }

    internal static RedisKey CreateKey(string consumerKeyId) =>
        KeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(consumerKeyId)));

    // Both counters use one Redis Cluster slot so Lua can update them atomically.
    internal const string PreAuthenticationGlobalKey = "storyvoice:external-voice:{pre-auth:v1}:global";

    internal static RedisKey CreatePreAuthenticationSourceKey(int bucket) =>
        $"storyvoice:external-voice:{{pre-auth:v1}}:source:{bucket}";

    private static ExternalVoiceSynthesisException Unavailable() =>
        new(ExternalVoiceSynthesisFailureKind.SynthesisUnavailable, retryAfterSeconds: 30);
}

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
        local value = redis.call('GET', KEYS[1])
        if not value then
            redis.call('SET', KEYS[1], 1, 'PX', 60000)
            return {1, 0}
        end
        local count = tonumber(value)
        local ttl = redis.call('PTTL', KEYS[1])
        if not count or count < 1 or count ~= math.floor(count) or ttl <= 0 or ttl > 60000 then
            return {-1, 0}
        end
        if count >= tonumber(ARGV[1]) then
            return {0, math.ceil(ttl / 1000)}
        end
        redis.call('INCR', KEYS[1])
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

        try
        {
            var database = connectionFactory().GetDatabase();
            var result = await database.ScriptEvaluateAsync(
                    AcquireScript,
                    [CreateKey(consumerKeyId)],
                    [currentOptions.RequestsPerMinute],
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

    private static ExternalVoiceSynthesisException Unavailable() =>
        new(ExternalVoiceSynthesisFailureKind.SynthesisUnavailable, retryAfterSeconds: 30);
}

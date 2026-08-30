using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;

namespace StoryVoice.Api;

public sealed class ExternalVoiceRequestRateLimiter(IOptions<ExternalVoiceApiOptions> options)
    : IExternalVoiceRequestRateLimiter, IDisposable
{
    private readonly ConcurrentDictionary<string, FixedWindowRateLimiter> limiters =
        new(StringComparer.Ordinal);

    public RateLimitPartition<string> GetPartition(string consumerKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerKeyId);
        return RateLimitPartition.Get(consumerKeyId, _ => GetLimiter(consumerKeyId));
    }

    public bool TryAcquire(string consumerKeyId, out int retryAfterSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerKeyId);
        using var lease = GetLimiter(consumerKeyId).AttemptAcquire(permitCount: 1);
        if (lease.IsAcquired)
        {
            retryAfterSeconds = 0;
            return true;
        }

        retryAfterSeconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Clamp(Math.Ceiling(retryAfter.TotalSeconds), 1, 60)
            : 60;
        return false;
    }

    private FixedWindowRateLimiter GetLimiter(string consumerKeyId) =>
        limiters.GetOrAdd(
            consumerKeyId,
            _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, options.Value.RequestsPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }));

    public void Dispose()
    {
        foreach (var limiter in limiters.Values)
        {
            limiter.Dispose();
        }
    }
}

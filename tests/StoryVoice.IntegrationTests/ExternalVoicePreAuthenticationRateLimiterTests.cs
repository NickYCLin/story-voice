using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using StoryVoice.Api;
using StoryVoice.Infrastructure.ExternalVoices;

namespace StoryVoice.IntegrationTests;

public sealed class ExternalVoicePreAuthenticationRateLimiterTests
{
    [Fact]
    public void Source_normalization_groups_mapped_ipv4_ipv6_prefixes_and_unknown_sources()
    {
        using (var limiter = CreateLimiter(sourceLimit: 1, globalLimit: 20))
        {
            Assert.True(limiter.TryAcquire(IPAddress.Parse("192.0.2.10"), out _));
            Assert.False(limiter.TryAcquire(IPAddress.Parse("::ffff:192.0.2.10"), out var retryAfter));
            Assert.InRange(retryAfter, 1, 60);
        }

        using (var limiter = CreateLimiter(sourceLimit: 1, globalLimit: 20))
        {
            Assert.True(limiter.TryAcquire(IPAddress.Parse("2001:db8:1:2::1"), out _));
            Assert.False(limiter.TryAcquire(IPAddress.Parse("2001:db8:1:2:ffff::99"), out _));
        }

        using (var limiter = CreateLimiter(sourceLimit: 1, globalLimit: 20))
        {
            Assert.True(limiter.TryAcquire(sourceAddress: null, out _));
            Assert.False(limiter.TryAcquire(sourceAddress: null, out _));
        }
    }

    [Fact]
    public void Global_limit_bounds_rotating_sources_without_allocating_source_partitions()
    {
        using var limiter = CreateLimiter(sourceLimit: 10, globalLimit: 2);

        Assert.True(limiter.TryAcquire(IPAddress.Parse("192.0.2.1"), out _));
        Assert.True(limiter.TryAcquire(IPAddress.Parse("198.51.100.1"), out _));
        Assert.False(limiter.TryAcquire(IPAddress.Parse("203.0.113.1"), out var retryAfter));
        Assert.InRange(retryAfter, 1, 60);

        var sourceLimiterField = typeof(ExternalVoicePreAuthenticationRateLimiter)
            .GetField("sourceLimiters", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        var sourceLimiters = Assert.IsType<FixedWindowRateLimiter[]>(
            sourceLimiterField?.GetValue(limiter));
        Assert.Equal(256, sourceLimiters.Length);
    }

    private static ExternalVoicePreAuthenticationRateLimiter CreateLimiter(
        int sourceLimit,
        int globalLimit) =>
        new(Options.Create(new ExternalVoiceApiOptions
        {
            PreAuthenticationRequestsPerMinute = sourceLimit,
            PreAuthenticationGlobalRequestsPerMinute = globalLimit,
        }));
}

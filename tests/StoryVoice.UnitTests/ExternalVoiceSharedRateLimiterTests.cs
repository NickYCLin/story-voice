using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;

namespace StoryVoice.UnitTests;

public sealed class ExternalVoiceSharedRateLimiterTests
{
    [Fact]
    public async Task Disabled_shared_limits_do_not_resolve_a_redis_connection()
    {
        var limiter = new RedisExternalVoiceSharedRateLimiter(Options.Create(new ExternalVoiceApiOptions()),
            () => throw new InvalidOperationException("Must not resolve Redis"));
        await limiter.EnsureAllowedAsync("consumer_01", TestContext.Current.CancellationToken);
        await limiter.EnsurePreAuthenticationAllowedAsync(null, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("INVALID")]
    [InlineData("")]
    public async Task Unknown_consumers_never_allocate_redis_keys(string consumerKeyId)
    {
        var limiter = new RedisExternalVoiceSharedRateLimiter(Options.Create(new ExternalVoiceApiOptions { SharedRateLimitEnabled = true }),
            () => throw new InvalidOperationException("Must not resolve Redis"));
        var failure = await Assert.ThrowsAsync<ExternalVoiceSynthesisException>(() =>
            limiter.EnsureAllowedAsync(consumerKeyId, TestContext.Current.CancellationToken));
        Assert.Equal(ExternalVoiceSynthesisFailureKind.VoiceNotAvailable, failure.FailureKind);
    }

    [Fact]
    public async Task Connection_failure_is_unavailable_without_private_diagnostics()
    {
        var options = Options.Create(new ExternalVoiceApiOptions
        {
            SharedRateLimitEnabled = true,
            Consumers = new() { ["consumer_01"] = new() { AccessTier = ExternalVoiceAccessTiers.PrivateDevelopment } },
        });
        var limiter = new RedisExternalVoiceSharedRateLimiter(options,
            () => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "private-host.invalid"));
        var failure = await Assert.ThrowsAsync<ExternalVoiceSynthesisException>(() =>
            limiter.EnsureAllowedAsync("consumer_01", TestContext.Current.CancellationToken));
        Assert.Equal(ExternalVoiceSynthesisFailureKind.SynthesisUnavailable, failure.FailureKind);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private-host", failure.Message);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_does_not_spend_a_permit()
    {
        var limiter = new RedisExternalVoiceSharedRateLimiter(Options.Create(new ExternalVoiceApiOptions { SharedRateLimitEnabled = true }),
            () => throw new InvalidOperationException("Must not resolve Redis"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.EnsureAllowedAsync("consumer_01", new CancellationToken(true)));
    }
}

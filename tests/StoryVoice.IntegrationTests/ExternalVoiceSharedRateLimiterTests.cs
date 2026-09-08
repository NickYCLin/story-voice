using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;

namespace StoryVoice.IntegrationTests;

public sealed class ExternalVoiceSharedRateLimiterTests(RedisRateLimitFixture redis)
    : IClassFixture<RedisRateLimitFixture>
{
    [Fact]
    public async Task Separate_connections_atomically_share_one_budget_and_isolate_consumers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var firstConnection = await redis.ConnectAsync();
        using var secondConnection = await redis.ConnectAsync();
        var key = $"consumer_{Guid.NewGuid():N}";
        var otherKey = $"consumer_{Guid.NewGuid():N}";
        var options = OptionsFor(key, otherKey);
        var first = new RedisExternalVoiceSharedRateLimiter(options, () => firstConnection);
        var second = new RedisExternalVoiceSharedRateLimiter(options, () => secondConnection);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(async index =>
        {
            try
            {
                await (index % 2 == 0 ? first : second).EnsureAllowedAsync(key, ct);
                return true;
            }
            catch (ExternalVoiceSynthesisException exception)
            {
                Assert.Equal(ExternalVoiceSynthesisFailureKind.RateLimited, exception.FailureKind);
                Assert.InRange(exception.RetryAfterSeconds!.Value, 1, 60);
                return false;
            }
        }));
        Assert.Equal(3, results.Count(allowed => allowed));
        var storageKey = RedisExternalVoiceSharedRateLimiter.CreateKey(key);
        Assert.DoesNotContain(key, storageKey.ToString());
        Assert.Equal("3", (string?)await firstConnection.GetDatabase().StringGetAsync(storageKey));
        var ttl = await firstConnection.GetDatabase().KeyTimeToLiveAsync(storageKey);
        Assert.InRange(ttl!.Value.TotalSeconds, 1, 60);
        await second.EnsureAllowedAsync(otherKey, ct);

        // A new process/connection cannot reset an exhausted consumer budget.
        using var thirdConnection = await redis.ConnectAsync();
        var restarted = new RedisExternalVoiceSharedRateLimiter(options, () => thirdConnection);
        var blocked = await Assert.ThrowsAsync<ExternalVoiceSynthesisException>(() => restarted.EnsureAllowedAsync(key, ct));
        Assert.Equal(ExternalVoiceSynthesisFailureKind.RateLimited, blocked.FailureKind);
    }

    [Fact]
    public async Task Expired_windows_reopen_but_malformed_or_persistent_counters_fail_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = await redis.ConnectAsync();
        var key = $"consumer_{Guid.NewGuid():N}";
        var limiter = new RedisExternalVoiceSharedRateLimiter(OptionsFor(key), () => connection);
        var database = connection.GetDatabase();
        var storageKey = RedisExternalVoiceSharedRateLimiter.CreateKey(key);
        foreach (var value in new[] { "private-invalid-value", "0", "1.5" })
        {
            await database.StringSetAsync(storageKey, value, TimeSpan.FromMinutes(1));
            var invalid = await Assert.ThrowsAsync<ExternalVoiceSynthesisException>(() => limiter.EnsureAllowedAsync(key, ct));
            Assert.Equal(ExternalVoiceSynthesisFailureKind.SynthesisUnavailable, invalid.FailureKind);
            Assert.DoesNotContain(value, invalid.Message);
            Assert.Equal(value, (string?)await database.StringGetAsync(storageKey));
        }

        await database.StringSetAsync(storageKey, 3);
        var persistent = await Assert.ThrowsAsync<ExternalVoiceSynthesisException>(() => limiter.EnsureAllowedAsync(key, ct));
        Assert.Equal(ExternalVoiceSynthesisFailureKind.SynthesisUnavailable, persistent.FailureKind);
        await database.KeyExpireAsync(storageKey, TimeSpan.FromMilliseconds(50));
        await Task.Delay(100, ct);
        await limiter.EnsureAllowedAsync(key, ct);
        Assert.Equal("1", (string?)await database.StringGetAsync(storageKey));
    }

    [Fact]
    public async Task A_stalled_redis_command_returns_unavailable_without_a_local_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = await redis.ConnectAsync();
        var key = $"consumer_{Guid.NewGuid():N}";
        var limiter = new RedisExternalVoiceSharedRateLimiter(OptionsFor(key), () => connection);
        await connection.GetDatabase().ExecuteAsync("CLIENT", "PAUSE", "4000", "ALL");
        var failure = await Assert.ThrowsAsync<ExternalVoiceSynthesisException>(() => limiter.EnsureAllowedAsync(key, ct));
        Assert.Equal(ExternalVoiceSynthesisFailureKind.SynthesisUnavailable, failure.FailureKind);
        Assert.Equal(30, failure.RetryAfterSeconds);
        Assert.Null(failure.InnerException);
        await connection.GetDatabase().PingAsync();
    }

    private static IOptions<ExternalVoiceApiOptions> OptionsFor(params string[] keys) => Options.Create(new ExternalVoiceApiOptions
    {
        SharedRateLimitEnabled = true,
        RequestsPerMinute = 3,
        Consumers = keys.ToDictionary(key => key, _ => new ExternalVoiceConsumerOptions { AccessTier = ExternalVoiceAccessTiers.PrivateDevelopment }),
    });
}

public sealed class RedisRateLimitFixture : IAsyncLifetime
{
    private readonly IContainer container = new ContainerBuilder("redis:7.4-alpine")
        .WithPortBinding(6379, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
        .Build();

    public async ValueTask InitializeAsync() => await container.StartAsync(TestContext.Current.CancellationToken);

    public Task<ConnectionMultiplexer> ConnectAsync() => ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
    {
        EndPoints = { { container.Hostname, container.GetMappedPublicPort(6379) } },
        AbortOnConnectFail = true,
        AsyncTimeout = 10_000,
        AllowAdmin = true,
    });

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}

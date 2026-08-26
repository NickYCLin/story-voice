using Microsoft.Extensions.Options;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;

namespace StoryVoice.UnitTests;

public sealed class ExternalVoiceIdempotencyCoordinatorTests
{
    private static readonly byte[] RequestHash = [1, 2, 3, 4];

    private static ExternalVoiceIdempotencyCoordinator CreateCoordinator(int cacheEntries = 32) =>
        new(
            Options.Create(new ExternalVoiceApiOptions { IdempotencyCacheEntries = cacheEntries }),
            TimeProvider.System);

    [Fact]
    public async Task Successful_result_is_replayed_without_reexecuting_factory()
    {
        var coordinator = CreateCoordinator();
        var calls = 0;

        var first = await coordinator.ExecuteAsync("consumer-a", "request_key_0001", RequestHash, () =>
        {
            calls++;
            return Task.FromResult(new ExternalVoiceAudio([9, 9, 9], "audio/wav", 1_250));
        });
        var second = await coordinator.ExecuteAsync("consumer-a", "request_key_0001", RequestHash, () =>
        {
            calls++;
            return Task.FromResult(new ExternalVoiceAudio([7, 7, 7], "audio/wav"));
        });

        Assert.Equal(1, calls);
        Assert.Equal(first.Content, second.Content);
        Assert.Equal(1_250, second.DurationMilliseconds);
    }

    [Fact]
    public async Task Same_key_with_different_request_hash_is_a_conflict()
    {
        var coordinator = CreateCoordinator();
        await coordinator.ExecuteAsync("consumer-a", "request_key_0002", RequestHash, () =>
            Task.FromResult(new ExternalVoiceAudio([1], "audio/wav")));

        var exception = await Assert.ThrowsAsync<ExternalVoiceSynthesisException>(() =>
            coordinator.ExecuteAsync("consumer-a", "request_key_0002", [9, 9, 9, 9], () =>
                Task.FromResult(new ExternalVoiceAudio([1], "audio/wav"))));
        Assert.Equal(ExternalVoiceSynthesisFailureKind.IdempotencyConflict, exception.FailureKind);
    }

    [Fact]
    public async Task Failed_result_releases_the_key_so_a_documented_same_key_retry_reexecutes()
    {
        var coordinator = CreateCoordinator();
        var calls = 0;

        var failure = await Assert.ThrowsAsync<ExternalVoiceSynthesisException>(() =>
            coordinator.ExecuteAsync("consumer-a", "request_key_0003", RequestHash, () =>
            {
                calls++;
                throw new ExternalVoiceSynthesisException(
                    ExternalVoiceSynthesisFailureKind.SynthesisUnavailable);
            }));
        Assert.Equal(ExternalVoiceSynthesisFailureKind.SynthesisUnavailable, failure.FailureKind);

        var retried = await coordinator.ExecuteAsync("consumer-a", "request_key_0003", RequestHash, () =>
        {
            calls++;
            return Task.FromResult(new ExternalVoiceAudio([5, 5], "audio/wav"));
        });

        Assert.Equal(2, calls);
        Assert.Equal([5, 5], retried.Content);
    }

    [Fact]
    public async Task Cancellation_of_the_creating_request_does_not_poison_later_retries()
    {
        var coordinator = CreateCoordinator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ExecuteAsync("consumer-a", "request_key_0004", RequestHash, () =>
                Task.FromCanceled<ExternalVoiceAudio>(new CancellationToken(canceled: true))));

        var retried = await coordinator.ExecuteAsync("consumer-a", "request_key_0004", RequestHash, () =>
            Task.FromResult(new ExternalVoiceAudio([3], "audio/wav")));
        Assert.Equal([3], retried.Content);
    }

    [Fact]
    public async Task Capacity_is_per_consumer_so_one_tenant_cannot_starve_another()
    {
        var coordinator = CreateCoordinator(cacheEntries: 2);
        for (var index = 0; index < 2; index++)
        {
            await coordinator.ExecuteAsync("consumer-a", $"request_key_010{index}", RequestHash, () =>
                Task.FromResult(new ExternalVoiceAudio([1], "audio/wav")));
        }

        var exception = await Assert.ThrowsAsync<ExternalVoiceSynthesisException>(() =>
            coordinator.ExecuteAsync("consumer-a", "request_key_0199", RequestHash, () =>
                Task.FromResult(new ExternalVoiceAudio([1], "audio/wav"))));
        Assert.Equal(ExternalVoiceSynthesisFailureKind.RateLimited, exception.FailureKind);
        Assert.NotNull(exception.RetryAfterSeconds);

        var otherConsumer = await coordinator.ExecuteAsync("consumer-b", "request_key_0200", RequestHash, () =>
            Task.FromResult(new ExternalVoiceAudio([2], "audio/wav")));
        Assert.Equal([2], otherConsumer.Content);
    }
}

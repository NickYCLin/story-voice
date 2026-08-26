using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using StoryVoice.Application.ExternalVoices;

namespace StoryVoice.Infrastructure.ExternalVoices;

internal sealed class ExternalVoiceIdempotencyCoordinator(
    IOptions<ExternalVoiceApiOptions> options,
    TimeProvider timeProvider)
{
    private readonly object sync = new();
    private readonly Dictionary<CacheKey, CacheEntry> entries = [];

    public Task<ExternalVoiceAudio> ExecuteAsync(
        string consumerKeyId,
        string idempotencyKey,
        byte[] requestHash,
        Func<Task<ExternalVoiceAudio>> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(requestHash);
        ArgumentNullException.ThrowIfNull(factory);

        CacheEntry entry;
        CacheKey key;
        TaskCompletionSource<ExternalVoiceAudio>? completion = null;
        var now = timeProvider.GetUtcNow();
        lock (sync)
        {
            RemoveExpired(now);
            key = new CacheKey(consumerKeyId, idempotencyKey);
            if (entries.TryGetValue(key, out entry!))
            {
                if (!CryptographicOperations.FixedTimeEquals(entry.RequestHash, requestHash))
                {
                    throw new ExternalVoiceSynthesisException(
                        ExternalVoiceSynthesisFailureKind.IdempotencyConflict);
                }
            }
            else
            {
                // The capacity is per consumer: a single tenant filling its own window
                // must not exhaust the cache and starve every other consumer's keys.
                if (CountConsumerEntries(consumerKeyId) >= options.Value.IdempotencyCacheEntries)
                {
                    throw new ExternalVoiceSynthesisException(
                        ExternalVoiceSynthesisFailureKind.RateLimited,
                        CalculateRetryAfterSeconds(consumerKeyId, now));
                }

                completion = new TaskCompletionSource<ExternalVoiceAudio>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                entry = new CacheEntry(
                    requestHash.ToArray(),
                    completion.Task,
                    DateTimeOffset.MaxValue);
                entries.Add(key, entry);
            }
        }

        if (completion is not null)
        {
            _ = CompleteAsync(key, entry, completion, factory);
        }

        return CloneResultAsync(entry.Task);
    }

    private async Task CompleteAsync(
        CacheKey key,
        CacheEntry entry,
        TaskCompletionSource<ExternalVoiceAudio> completion,
        Func<Task<ExternalVoiceAudio>> factory)
    {
        var removeImmediately = false;
        try
        {
            var audio = await factory().ConfigureAwait(false);
            completion.TrySetResult(Clone(audio));
        }
        catch (Exception exception)
        {
            // Only successful audio is pinned for TTL replay. The documented contract
            // tells clients to retry a failed request with the SAME Idempotency-Key,
            // so a transient failure (gateway 503, timeout, caller disconnect) must
            // release the key instead of replaying the stale failure for the TTL.
            removeImmediately = true;
            completion.TrySetException(exception);
        }
        finally
        {
            lock (sync)
            {
                if (removeImmediately)
                {
                    if (entries.TryGetValue(key, out var current)
                        && ReferenceEquals(current, entry))
                    {
                        entries.Remove(key);
                    }
                }
                else
                {
                    entry.ExpiresAtUtc = timeProvider.GetUtcNow().AddMinutes(
                        options.Value.IdempotencyTtlMinutes);
                }
            }
        }
    }

    private static async Task<ExternalVoiceAudio> CloneResultAsync(
        Task<ExternalVoiceAudio> task) =>
        Clone(await task.ConfigureAwait(false));

    private static ExternalVoiceAudio Clone(ExternalVoiceAudio audio) =>
        new(audio.Content.ToArray(), audio.ContentType, audio.DurationMilliseconds);

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in entries
                     .Where(candidate => candidate.Value.ExpiresAtUtc <= now)
                     .Select(candidate => candidate.Key)
                     .ToArray())
        {
            entries.Remove(key);
        }
    }

    private int CountConsumerEntries(string consumerKeyId) =>
        entries.Keys.Count(key =>
            string.Equals(key.ConsumerKeyId, consumerKeyId, StringComparison.Ordinal));

    private int CalculateRetryAfterSeconds(string consumerKeyId, DateTimeOffset now)
    {
        var earliestExpiry = entries
            .Where(pair =>
                string.Equals(pair.Key.ConsumerKeyId, consumerKeyId, StringComparison.Ordinal)
                && pair.Value.ExpiresAtUtc != DateTimeOffset.MaxValue)
            .Select(pair => pair.Value.ExpiresAtUtc)
            .DefaultIfEmpty(now.AddMinutes(1))
            .Min();
        var seconds = Math.Ceiling((earliestExpiry - now).TotalSeconds);
        return (int)Math.Clamp(seconds, 1, 600);
    }

    private readonly record struct CacheKey(
        string ConsumerKeyId,
        string IdempotencyKey);

    private sealed class CacheEntry(
        byte[] requestHash,
        Task<ExternalVoiceAudio> task,
        DateTimeOffset expiresAtUtc)
    {
        public byte[] RequestHash { get; } = requestHash;

        public Task<ExternalVoiceAudio> Task { get; } = task;

        public DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
    }
}

internal sealed class ExternalVoiceConcurrencyGate
{
    private int active;

    public IDisposable? TryEnter() =>
        Interlocked.CompareExchange(ref active, 1, 0) == 0
            ? new Lease(this)
            : null;

    private sealed class Lease(ExternalVoiceConcurrencyGate owner) : IDisposable
    {
        private ExternalVoiceConcurrencyGate? current = owner;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref current, null);
            if (gate is not null)
            {
                Volatile.Write(ref gate.active, 0);
            }
        }
    }
}

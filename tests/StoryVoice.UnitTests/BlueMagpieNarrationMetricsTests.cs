using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed partial class BlueMagpieMultiVoiceNarrationProviderTests
{
    [Fact]
    public async Task Metrics_distinguish_interrupted_cold_generation_partial_recovery_and_a_warm_cache()
    {
        using var capture = CaptureMetrics();
        var ct = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        var cache = CreatePersistentCache(Path.Combine(root, "cache"));
        var request = CreateRequest([NeutralTurn() with { Text = new string('甲', 121) }, NeutralTurn()]);
        try
        {
            var interrupted = CreateProvider(new FailOnCallClient(2), new RecordingComposer(), cache);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                interrupted.SynthesizeAsync(request, Path.Combine(root, "failed.mp3"), null, ct));
            var client = new RecordingClient();
            var provider = CreateProvider(client, new RecordingComposer(), cache);
            await provider.SynthesizeAsync(request, Path.Combine(root, "resumed.mp3"), null, ct);
            await provider.SynthesizeAsync(request, Path.Combine(root, "warm.mp3"), null, ct);

            Assert.Equal(2, client.Requests.Count);
            Assert.Equal(1, capture.Sum("attempts", "outcome", "provider_unavailable"));
            Assert.Equal(2, capture.Sum("attempts", "outcome", "success"));
            Assert.Equal(0, capture.ActiveAttempts);
            Assert.Equal(4, capture.Sum("resolved_chunks", "cache", "hit"));
            Assert.Equal(3, capture.Sum("resolved_chunks", "cache", "miss"));
            Assert.Equal(3, capture.Values("provider.duration", "outcome", "success").Count());
            Assert.Single(capture.Values("provider.duration", "outcome", "failed"));
            Assert.Equal(7, capture.Values("chunk.duration").Count());
            Assert.All(capture.Values("rendered_audio.duration"), value => Assert.Equal(3.75, value.Value));
            Assert.Equal(2, capture.Values("real_time_factor", "reused_cache", true).Count());
            Assert.All(capture.Values("real_time_factor"), value => Assert.True(value.Value >= 0 && double.IsFinite(value.Value)));
            Assert.All(capture.Measurements, value =>
            {
                Assert.True(double.IsFinite(value.Value));
                Assert.All(value.Tags, tag => Assert.Contains(tag.Key, new[] { "cache", "outcome", "reused_cache" }));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_releases_the_active_metric_and_does_not_report_completed_audio()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var root = CreateRoot();
        var client = new BlockingClient();
        try
        {
            var operation = CreateProvider(client, new RecordingComposer()).SynthesizeAsync(
                CreateRequest([NeutralTurn()]), Path.Combine(root, "cancelled.mp3"), null, cancelled.Token);
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancelled.Token);
            // Attaching monitoring after generation starts must still see the actual active count.
            using var capture = CaptureMetrics();
            Assert.Equal(1, capture.ActiveAttempts);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(0, capture.ActiveAttempts);
            Assert.Equal(1, capture.Sum("attempts", "outcome", "cancelled"));
            Assert.Single(capture.Values("provider.duration", "outcome", "cancelled"));
            Assert.Empty(capture.Values("rendered_audio.duration"));
            Assert.Empty(capture.Values("real_time_factor"));
        }
        finally
        {
            cancelled.Cancel();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rejected_contract_and_composition_failure_are_counted_without_successful_audio_metrics()
    {
        using var capture = CaptureMetrics();
        var ct = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            var invalid = CreateRequest([NeutralTurn() with { Voice = "not-a-pinned-voice" }]);
            await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
                CreateProvider(new RecordingClient(), new RecordingComposer()).SynthesizeAsync(
                    invalid, Path.Combine(root, "rejected.mp3"), null, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateProvider(new RecordingClient(), new ThrowingComposer()).SynthesizeAsync(
                    CreateRequest([NeutralTurn()]), Path.Combine(root, "failed.mp3"), null, ct));
            Assert.Equal(1, capture.Sum("attempts", "outcome", "rejected"));
            Assert.Equal(1, capture.Sum("attempts", "outcome", "failed"));
            Assert.Equal(0, capture.ActiveAttempts);
            Assert.Single(capture.Values("provider.duration"));
            Assert.Empty(capture.Values("real_time_factor"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_or_invalid_timing_never_fabricates_audio_duration()
    {
        Assert.Null(BlueMagpieNarrationMetrics.MeasureAudioSeconds(MultiVoiceSynthesisResult.None, 1));
        Assert.Null(BlueMagpieNarrationMetrics.MeasureAudioSeconds(new([new(0, 0, 100)]), 2));
        Assert.Null(BlueMagpieNarrationMetrics.MeasureAudioSeconds(new([new(0, 0, 0)]), 1));
        Assert.Null(BlueMagpieNarrationMetrics.MeasureAudioSeconds(new([new(0, long.MaxValue, 100)]), 1));
        Assert.Null(BlueMagpieNarrationMetrics.MeasureAudioSeconds(new([new(0, 0, 100), new(1, 50, 100)]), 2));
        Assert.Equal(1.5, BlueMagpieNarrationMetrics.MeasureAudioSeconds(new([new(0, 250, 1250)]), 1));
    }

    [Fact]
    public async Task Failure_while_closing_the_cache_scope_does_not_count_as_a_successful_attempt()
    {
        using var capture = CaptureMetrics();
        var root = CreateRoot();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateProvider(new RecordingClient(), new RecordingComposer(), new FailingDisposeCache()).SynthesizeAsync(
                    CreateRequest([NeutralTurn(), NeutralTurn()]), Path.Combine(root, "cleanup-failed.mp3"), null,
                    TestContext.Current.CancellationToken));
            Assert.Equal(1, capture.Sum("attempts", "outcome", "failed"));
            Assert.Equal(0, capture.Sum("attempts", "outcome", "success"));
            Assert.Equal(0, capture.ActiveAttempts);
            Assert.Empty(capture.Values("real_time_factor"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Final_log_reports_zero_progress_on_failure_without_logging_text_or_exception_details()
    {
        var root = CreateRoot();
        var logger = new SynthesisLogger();
        var request = CreateRequest([NeutralTurn() with { Text = "不可寫進紀錄的合成測試句子" }]);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateProvider(new RecordingClient { Failure = new InvalidOperationException("synthetic-sensitive-detail") },
                    new RecordingComposer(), logger: logger).SynthesizeAsync(request, Path.Combine(root, "failed.mp3"), null,
                        TestContext.Current.CancellationToken));
            var log = Assert.Single(logger.Entries);
            Assert.Equal("failed", log["Outcome"]);
            Assert.Equal(0, log["ResolvedChunks"]);
            Assert.Equal(1, log["TotalChunks"]);
            Assert.Null(log["RealTimeFactor"]);
            var values = string.Join(" ", log.Values);
            Assert.DoesNotContain(request.Turns[0].Text, values, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-sensitive-detail", values, StringComparison.Ordinal);
            Assert.DoesNotContain(root, values, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SynthesisLogger : ILogger<BlueMagpieMultiVoiceNarrationProvider>
    {
        public List<Dictionary<string, object?>> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Entries.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary());
        }
    }

    private sealed class FailingDisposeCache : IBlueMagpieChunkCache
    {
        public async Task<IBlueMagpieChunkCacheScope> OpenScopeAsync(NarrationSynthesisCacheContext context, CancellationToken ct) =>
            new FailingScope(await new EphemeralChunkCache().OpenScopeAsync(context, ct));
        public Task CleanupAsync(CancellationToken ct) => Task.CompletedTask;
        private sealed class FailingScope(IBlueMagpieChunkCacheScope inner) : IBlueMagpieChunkCacheScope
        {
            public Task<BlueMagpieChunkCacheEntry> GetOrCreateAsync(BlueMagpieChunkCacheRequest request,
                Func<CancellationToken, Task<byte[]>> createAudio, CancellationToken ct) =>
                inner.GetOrCreateAsync(request, createAudio, ct);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                throw new IOException("synthetic cleanup failure");
            }
        }
    }

    private MetricsCapture CaptureMetrics() => new(_metricsServices.GetRequiredService<IMeterFactory>()
        .Create(BlueMagpieNarrationMetrics.MeterName));

    private sealed record Measurement(string Name, double Value, KeyValuePair<string, object?>[] Tags);

    private sealed class MetricsCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<Measurement> Measurements { get; } = new();

        public MetricsCapture(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Enqueue(new(instrument.Name, value, tags.ToArray())));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Measurements.Enqueue(new(instrument.Name, value, tags.ToArray())));
            _listener.Start();
        }

        public IEnumerable<Measurement> Values(string name, string? tag = null, object? tagValue = null) =>
            Measurements.Where(value => value.Name == $"storyvoice.bluemagpie.{name}"
                && (tag is null || value.Tags.Any(item => item.Key == tag && Equals(item.Value, tagValue))));

        public double Sum(string name, string? tag = null, object? tagValue = null) =>
            Values(name, tag, tagValue).Sum(value => value.Value);

        public double ActiveAttempts
        {
            get
            {
                _listener.RecordObservableInstruments();
                return Values("active_attempts").Last().Value;
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}

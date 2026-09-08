using System.Diagnostics.Metrics;

namespace StoryVoice.Worker;

/// <summary>Attempt-level diagnostics with bounded tags; never attach book text or owner/job IDs.</summary>
public sealed class BlueMagpieNarrationMetrics
{
    public const string MeterName = "StoryVoice.BlueMagpie";
    private long _activeAttempts;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _chunks;
    private readonly Counter<long> _resolvedBytes;
    private readonly Histogram<double> _attemptSeconds;
    private readonly Histogram<double> _chunkSeconds;
    private readonly Histogram<double> _providerSeconds;
    private readonly Histogram<double> _audioSeconds;
    private readonly Histogram<double> _realTimeFactor;

    public BlueMagpieNarrationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _ = meter.CreateObservableGauge("storyvoice.bluemagpie.active_attempts",
            () => Interlocked.Read(ref _activeAttempts), "{attempt}");
        _attempts = meter.CreateCounter<long>("storyvoice.bluemagpie.attempts", "{attempt}");
        _chunks = meter.CreateCounter<long>("storyvoice.bluemagpie.resolved_chunks", "{chunk}");
        _resolvedBytes = meter.CreateCounter<long>("storyvoice.bluemagpie.resolved_audio_bytes", "By");
        _attemptSeconds = meter.CreateHistogram<double>("storyvoice.bluemagpie.attempt.duration", "s");
        _chunkSeconds = meter.CreateHistogram<double>("storyvoice.bluemagpie.chunk.duration", "s");
        _providerSeconds = meter.CreateHistogram<double>("storyvoice.bluemagpie.provider.duration", "s");
        _audioSeconds = meter.CreateHistogram<double>("storyvoice.bluemagpie.rendered_audio.duration", "s");
        _realTimeFactor = meter.CreateHistogram<double>("storyvoice.bluemagpie.real_time_factor", "1");
    }

    internal void StartAttempt() => Interlocked.Increment(ref _activeAttempts);

    internal void ResolveChunk(TimeSpan elapsed, bool cacheHit, long bytes)
    {
        var tag = new KeyValuePair<string, object?>("cache", cacheHit ? "hit" : "miss");
        _chunks.Add(1, tag);
        _resolvedBytes.Add(bytes, tag);
        _chunkSeconds.Record(elapsed.TotalSeconds, tag);
    }

    internal void RecordProviderCall(TimeSpan elapsed, string outcome) =>
        _providerSeconds.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));

    internal void FinishAttempt(TimeSpan elapsed, string outcome, double? audioSeconds, bool reusedCache)
    {
        Interlocked.Decrement(ref _activeAttempts);
        var tag = new KeyValuePair<string, object?>("outcome", outcome);
        _attempts.Add(1, tag);
        _attemptSeconds.Record(elapsed.TotalSeconds, tag);
        if (outcome == "success" && audioSeconds is > 0 && double.IsFinite(audioSeconds.Value))
        {
            var cacheTag = new KeyValuePair<string, object?>("reused_cache", reusedCache);
            _audioSeconds.Record(audioSeconds.Value, cacheTag);
            _realTimeFactor.Record(elapsed.TotalSeconds / audioSeconds.Value, cacheTag);
        }
    }

    internal static double? MeasureAudioSeconds(MultiVoiceSynthesisResult result, int turnCount)
    {
        if (result.TurnTimings is not { Count: > 0 } timings || timings.Count != turnCount) return null;
        long endMs = 0;
        for (var index = 0; index < timings.Count; index++)
        {
            var timing = timings[index];
            if (timing.TurnIndex != index || timing.StartMs < endMs || timing.DurationMs <= 0
                || timing.StartMs > long.MaxValue - timing.DurationMs) return null;
            endMs = timing.StartMs + timing.DurationMs;
        }
        return endMs / 1000d;
    }
}

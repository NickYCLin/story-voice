using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class VoAiConcurrencyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"storyvoice-voai-parallel-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly ControlledClient _client = new();
    private readonly CapturingComposer _composer = new();
    private Task<MultiVoiceSynthesisResult>? _running;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _client.ReleaseAll();
        if (_running is not null)
        {
            try { await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { /* Assertions inspect the operation result before fixture cleanup. */ }
        }
        _lifetime.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Parallel_chunks_refill_slots_and_keep_source_order_parameters_and_progress()
    {
        var progress = new List<NarrationSynthesisProgress>();
        var request = new MultiVoiceNarrationRequest([
            new(new string('甲', 1001), "v1:Neo:佑希:預設", "+10%", "+20Hz", "-5%", 250),
            new("乙", "v1:Neo:佑希:預設", "-10%", "-5Hz", "+5%", 400),
        ]);
        Start(request, new() { MaximumConcurrentChunks = 2 }, (value, _) =>
        {
            progress.Add(value);
            return Task.CompletedTask;
        });
        var calls = new[] { await _client.NextAsync(), await _client.NextAsync() };
        var first = Assert.Single(calls, call => call.Request.Text.Length == 1000);
        var second = Assert.Single(calls, call => call.Request.Text.Length == 1);
        Assert.Equal(2, _client.Started);
        second.Complete();
        var third = await _client.NextAsync();
        Assert.Equal("乙", third.Request.Text);
        Assert.Equal(2, _client.MaximumActive);
        third.Complete();
        first.Complete();
        await _running!;

        Assert.Equal([new string('甲', 1000), "甲", "乙"], _composer.Texts);
        Assert.Equal([0, 0, 1], _composer.Segments.Select(segment => segment.TurnIndex));
        Assert.Equal([250, 0, 400], _composer.Segments.Select(segment => segment.PauseBeforeMs));
        Assert.Equal(["-5%", "-5%", "+5%"], _composer.Segments.Select(segment => segment.Volume));
        Assert.Equal(1.1, first.Request.Speed);
        Assert.Equal(4, first.Request.PitchShift);
        Assert.Equal([new(1, 3), new(2, 3), new(3, 3)], progress);
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    [Fact]
    public async Task Default_settings_keep_a_single_chunk_in_flight()
    {
        Start(Request(3));
        for (var index = 1; index <= 3; index++)
        {
            var call = await _client.NextAsync();
            Assert.Equal(index, _client.Started);
            call.Complete();
        }
        await _running!;
        Assert.Equal(1, _client.MaximumActive);
    }

    [Fact]
    public async Task Parallel_progress_callbacks_are_serialized_and_monotonic()
    {
        var active = 0;
        var completed = new List<int>();
        Start(Request(4), new() { MaximumConcurrentChunks = 4 }, async (progress, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            await Task.Delay(10, token);
            completed.Add(progress.CompletedChunks);
            Interlocked.Decrement(ref active);
        });
        var calls = new[] { await _client.NextAsync(), await _client.NextAsync(), await _client.NextAsync(), await _client.NextAsync() };
        foreach (var call in calls) call.Complete();
        await _running!;
        Assert.Equal(0, active);
        Assert.Equal([1, 2, 3, 4], completed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Invalid_concurrency_fails_before_any_paid_request(int concurrency)
    {
        Start(Request(2), new() { MaximumConcurrentChunks = concurrency });
        await Assert.ThrowsAsync<PermanentNarrationProviderException>(() => _running!);
        Assert.Equal(0, _client.Started);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Failure_cancels_siblings_and_drains_open_files_before_cleanup()
    {
        _client.HoldCancellationDrain = true;
        Start(Request(4), new() { MaximumConcurrentChunks = 2 });
        var first = await _client.NextAsync();
        _ = await _client.NextAsync();
        first.Fail(new HttpRequestException("synthetic provider failure"));
        await _client.CancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(_running!.IsCompleted);
        Assert.Single(Directory.EnumerateDirectories(_root));
        _client.AllowDrain.TrySetResult();
        var error = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() => _running);
        Assert.Equal("voai_provider_failed", error.ErrorCode);
        Assert.Equal(2, _client.Started);
        Assert.Equal(0, _client.Active);
        Assert.Empty(_composer.Segments);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Caller_cancellation_drains_all_chunks_without_composition_or_new_requests()
    {
        Start(Request(4), new() { MaximumConcurrentChunks = 2 });
        _ = await _client.NextAsync();
        _ = await _client.NextAsync();
        await _lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _running!);
        Assert.Equal(2, _client.Started);
        Assert.Equal(0, _client.Active);
        Assert.Empty(_composer.Segments);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Concurrent_responses_share_one_job_budget_and_cannot_publish_over_budget_audio()
    {
        Start(Request(2), new() { MaximumConcurrentChunks = 2, MaximumJobResponseBytes = 10 });
        var first = await _client.NextAsync();
        var second = await _client.NextAsync();
        first.Complete();
        second.Complete();
        var error = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() => _running!);
        Assert.Contains("aggregate audio budget", error.InnerException!.Message);
        Assert.Equal(0, _client.Active);
        Assert.Empty(_composer.Segments);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Progress_failure_stops_remaining_chunks_without_replaying_completed_requests()
    {
        Start(Request(4), new() { MaximumConcurrentChunks = 2 }, (_, _) => throw new InvalidOperationException("progress failed"));
        var first = await _client.NextAsync();
        _ = await _client.NextAsync();
        first.Complete();
        await Assert.ThrowsAsync<PermanentNarrationProviderException>(() => _running!);
        Assert.Equal(2, _client.Started);
        Assert.Equal(0, _client.Active);
        Assert.Empty(_composer.Segments);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    private void Start(MultiVoiceNarrationRequest request, VoAiOptions? options = null,
        Func<NarrationSynthesisProgress, CancellationToken, Task>? progress = null)
    {
        var provider = new VoAiMultiVoiceNarrationProvider(_client, _composer,
            Options.Create(options ?? new()), NullLogger<VoAiMultiVoiceNarrationProvider>.Instance);
        _running = provider.SynthesizeAsync(request, Path.Combine(_root, "result.mp3"), progress, _lifetime.Token);
    }

    private static MultiVoiceNarrationRequest Request(int count) => new(Enumerable.Range(1, count)
        .Select(index => new NarrationTurn($"片段{index}", "v1:Neo:佑希:預設", "+0%", "+0Hz", "+0%", 0)).ToArray());

    private sealed class Call(VoAiSpeechSynthesisRequest request)
    {
        public VoAiSpeechSynthesisRequest Request { get; } = request;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete() => Completion.TrySetResult();
        public void Fail(Exception error) => Completion.TrySetException(error);
    }

    private sealed class ControlledClient : IVoAiTtsClient
    {
        private readonly Channel<Call> _started = Channel.CreateUnbounded<Call>();
        private readonly ConcurrentBag<Call> _calls = [];
        private int _active;
        private int _maximumActive;
        public int Started => _calls.Count;
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximumActive);
        public bool HoldCancellationDrain { get; set; }
        public TaskCompletionSource CancellationSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SynthesizeWavAsync(VoAiSpeechSynthesisRequest request, Stream destination, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = Volatile.Read(ref _maximumActive); }
            while (active > observed && Interlocked.CompareExchange(ref _maximumActive, active, observed) != observed);
            var call = new Call(request);
            _calls.Add(call);
            _started.Writer.TryWrite(call);
            try
            {
                await call.Completion.Task.WaitAsync(cancellationToken);
                await destination.WriteAsync(Encoding.UTF8.GetBytes(request.Text), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationSeen.TrySetResult();
                if (HoldCancellationDrain) await AllowDrain.Task;
                throw;
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public Task<Call> NextAsync() => _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public void ReleaseAll()
        {
            AllowDrain.TrySetResult();
            foreach (var call in _calls) call.Complete();
        }
    }

    private sealed class CapturingComposer : IVoAiAudioComposer
    {
        public IReadOnlyList<VoAiAudioSegment> Segments { get; private set; } = [];
        public string[] Texts { get; private set; } = [];
        public async Task<MultiVoiceSynthesisResult> ComposeAsync(IReadOnlyList<VoAiAudioSegment> segments, string outputPath, CancellationToken cancellationToken)
        {
            Segments = segments.ToArray();
            Texts = await Task.WhenAll(segments.Select(segment => File.ReadAllTextAsync(segment.InputWavPath, cancellationToken)));
            await File.WriteAllTextAsync(outputPath, "synthetic composed audio", cancellationToken);
            return new([]);
        }
    }
}

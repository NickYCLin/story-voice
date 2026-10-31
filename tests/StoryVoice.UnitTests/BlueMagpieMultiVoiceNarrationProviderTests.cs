using System.Text;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using StoryVoice.Application.Series;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed partial class BlueMagpieMultiVoiceNarrationProviderTests : IDisposable
{
    private readonly ServiceProvider _metricsServices = new ServiceCollection().AddMetrics()
        .AddSingleton<BlueMagpieNarrationMetrics>().BuildServiceProvider();

    public void Dispose() => _metricsServices.Dispose();

    [Fact]
    public void Job_budget_defaults_preserve_the_existing_formal_runtime_contract()
    {
        var options = new BlueMagpieOptions();

        Assert.Equal(10_000, options.MaximumChunksPerJob);
        Assert.Equal(8L * 1024 * 1024 * 1024, options.MaximumJobAudioBytes);
        Assert.Equal(120, BlueMagpieOptions.MaximumTextScalarsPerChunk);
        Assert.Equal(61, BlueMagpieOptions.MinimumTextScalarsPerNonFinalChunk);
    }

    [Fact]
    public void Provider_identity_is_the_exact_BM1_runtime_contract()
    {
        var provider = CreateProvider(new RecordingClient(), new RecordingComposer());

        Assert.Equal("bluemagpie", provider.ProviderName);
        Assert.Equal(
            "bm1-d2d7ef3e81456915eb7a3cfe2446a9f19417c21b",
            provider.ProviderVersion);
        Assert.NotEqual(BlueMagpieOptions.PinnedModelRevision, provider.ProviderVersion);
    }

    [Fact]
    public async Task SynthesizeAsync_chunks_at_120_scalars_and_composes_48khz_with_turn_policies()
    {
        var root = CreateRoot();
        var outputPath = Path.Combine(root, "book.mp3");
        var client = new RecordingClient();
        var composer = new RecordingComposer();
        var provider = CreateProvider(client, composer);
        var progress = new List<NarrationSynthesisProgress>();
        var request = CreateRequest(
        [
            new NarrationTurn(
                new string('甲', 121),
                BlueMagpieOptions.FemaleVoice,
                "+0%",
                "+0Hz",
                "-5%",
                250),
            new NarrationTurn(
                "你好",
                BlueMagpieOptions.MaleVoice,
                "+0%",
                "+0Hz",
                "+0%",
                500),
        ]);

        try
        {
            var result = await provider.SynthesizeAsync(
                request,
                outputPath,
                (value, _) =>
                {
                    progress.Add(value);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.Collection(
                client.Requests,
                item =>
                {
                    Assert.Equal(120, item.Text.EnumerateRunes().Count());
                    Assert.Equal(BlueMagpieOptions.FemaleVoice, item.Voice);
                },
                item =>
                {
                    Assert.Single(item.Text.EnumerateRunes());
                    Assert.Equal(BlueMagpieOptions.FemaleVoice, item.Voice);
                },
                item =>
                {
                    Assert.Equal("你好", item.Text);
                    Assert.Equal(BlueMagpieOptions.MaleVoice, item.Voice);
                });
            Assert.Equal(48_000, composer.OutputSampleRate);
            Assert.Same(composer.Result, result);
            Assert.Equal(new[] { 0, 0, 1 }, composer.Segments.Select(segment => segment.TurnIndex));
            Assert.Collection(
                composer.Segments,
                item =>
                {
                    Assert.Equal(250, item.PauseBeforeMs);
                    Assert.Equal("-5%", item.Volume);
                },
                item =>
                {
                    Assert.Equal(0, item.PauseBeforeMs);
                    Assert.Equal("-5%", item.Volume);
                },
                item =>
                {
                    Assert.Equal(500, item.PauseBeforeMs);
                    Assert.Equal("+0%", item.Volume);
                });
            Assert.Collection(
                progress,
                value => Assert.Equal(new NarrationSynthesisProgress(1, 3), value),
                value => Assert.Equal(new NarrationSynthesisProgress(2, 3), value),
                value => Assert.Equal(new NarrationSynthesisProgress(3, 3), value));
            Assert.True(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateDirectories(root, "bluemagpie-tts-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SplitText_prefers_Chinese_punctuation_and_never_splits_surrogate_pairs()
    {
        var punctuationText = new string('甲', 100) + "。" + new string('乙', 30);
        var punctuationChunks = BlueMagpieMultiVoiceNarrationProvider.SplitText(punctuationText);
        var emojiChunks = BlueMagpieMultiVoiceNarrationProvider.SplitText(
            string.Concat(Enumerable.Repeat("😀", 121)));

        Assert.Collection(
            punctuationChunks,
            first => Assert.Equal(new string('甲', 100) + "。", first),
            second => Assert.Equal(new string('乙', 30), second));
        Assert.Equal(2, emojiChunks.Count);
        Assert.All(
            emojiChunks,
            chunk => Assert.InRange(
                chunk.EnumerateRunes().Count(),
                1,
                BlueMagpieMultiVoiceNarrationProvider.MaximumTextScalarsPerChunk));
        Assert.Equal(121, emojiChunks.Sum(chunk => chunk.EnumerateRunes().Count()));
    }

    [Fact]
    public async Task SynthesizeAsync_uses_the_configured_chunk_budget_before_HTTP()
    {
        var client = new RecordingClient();
        var composer = new RecordingComposer();
        var provider = CreateProvider(
            client,
            composer,
            providerOptions: new BlueMagpieOptions { MaximumChunksPerJob = 1 });

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            provider.SynthesizeAsync(
                CreateRequest([
                    NeutralTurn() with { Text = new string('甲', 121) },
                ]),
                "unused.mp3",
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal("bluemagpie_provider_contract_invalid", exception.ErrorCode);
        Assert.Empty(client.Requests);
        Assert.Empty(composer.Segments);
    }

    [Fact]
    public async Task SynthesizeAsync_uses_the_configured_aggregate_audio_budget()
    {
        var root = CreateRoot();
        var client = new RecordingClient();
        var composer = new RecordingComposer();
        var provider = CreateProvider(
            client,
            composer,
            cache: new ReportedSizeChunkCache((64L * 1024 * 1024) + 1),
            providerOptions: new BlueMagpieOptions { MaximumJobAudioBytes = 64L * 1024 * 1024 });

        try
        {
            var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
                provider.SynthesizeAsync(
                    CreateRequest([NeutralTurn()]),
                    Path.Combine(root, "over-budget.mp3"),
                    null,
                    TestContext.Current.CancellationToken));

            Assert.Equal("bluemagpie_provider_contract_invalid", exception.ErrorCode);
            Assert.Empty(client.Requests);
            Assert.Empty(composer.Segments);
            Assert.False(File.Exists(Path.Combine(root, "over-budget.mp3")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown", "+0%", "+0Hz")]
    [InlineData("female_voice", "+1%", "+0Hz")]
    [InlineData("female_voice", "+0%", "+1Hz")]
    public async Task SynthesizeAsync_rejects_voice_or_unsupported_prosody_before_HTTP(
        string voice,
        string rate,
        string pitch)
    {
        var client = new RecordingClient();
        var composer = new RecordingComposer();
        var provider = CreateProvider(client, composer);
        var request = CreateRequest(
            [new NarrationTurn("你好", voice, rate, pitch, "+0%", 0)]);

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            provider.SynthesizeAsync(request, "unused.mp3", null, CancellationToken.None));

        Assert.Equal("bluemagpie_provider_contract_invalid", exception.ErrorCode);
        Assert.Empty(client.Requests);
        Assert.Empty(composer.Segments);
    }

    [Fact]
    public async Task Dispatcher_rejects_a_character_version_mismatch_before_HTTP()
    {
        var client = new RecordingClient();
        var provider = CreateProvider(client, new RecordingComposer());
        var dispatcher = new NarrationProviderDispatcher(new NarrationProviderRegistry([provider]));
        var request = CreateRequest(
            [NeutralTurn()],
            characterContracts:
            [
                new NarrationProviderContract(
                    provider.ProviderName,
                    BlueMagpieOptions.PinnedModelRevision),
            ]);

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            dispatcher.SynthesizeAsync(
                provider.ProviderName,
                request,
                "unused.mp3",
                null,
                CancellationToken.None));

        Assert.Equal("provider_version_mismatch", exception.ErrorCode);
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData("BLUEMAGPIE", "bm1-d2d7ef3e81456915eb7a3cfe2446a9f19417c21b")]
    [InlineData("bluemagpie", "6f7cab914a1e27c56b504ec663c0144dc25cc0a3")]
    public async Task Dispatcher_requires_the_exact_provider_name_and_runtime_version_before_HTTP(
        string requestedProvider,
        string requestedVersion)
    {
        var client = new RecordingClient();
        var provider = CreateProvider(client, new RecordingComposer());
        var dispatcher = new NarrationProviderDispatcher(new NarrationProviderRegistry([provider]));
        var request = new MultiVoiceNarrationRequest(
            [NeutralTurn()],
            new NarrationProviderContract(requestedProvider, requestedVersion),
            []);

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            dispatcher.SynthesizeAsync(
                requestedProvider,
                request,
                "unused.mp3",
                null,
                CancellationToken.None));

        Assert.Equal("provider_version_mismatch", exception.ErrorCode);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Gateway_unavailability_is_retryable_and_cleans_incomplete_artifacts()
    {
        var root = CreateRoot();
        var outputPath = Path.Combine(root, "book.mp3");
        var client = new RecordingClient { Failure = new SeriesVoicePreviewUnavailableException() };
        var provider = CreateProvider(client, new RecordingComposer());

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.SynthesizeAsync(
                    CreateRequest([NeutralTurn()]),
                    outputPath,
                    null,
                    CancellationToken.None));

            Assert.IsNotType<PermanentNarrationProviderException>(exception);
            Assert.Equal("bluemagpie_provider_unavailable", exception.Message);
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateDirectories(root, "bluemagpie-tts-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gateway_contract_violation_is_permanent_and_never_composes()
    {
        var client = new RecordingClient
        {
            Failure = new SeriesVoicePreviewUnavailableException(
                SeriesVoicePreviewFailureKind.ContractViolation),
        };
        var composer = new RecordingComposer();
        var provider = CreateProvider(client, composer);

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            provider.SynthesizeAsync(
                CreateRequest([NeutralTurn()]),
                "unused.mp3",
                null,
                CancellationToken.None));

        Assert.Equal("bluemagpie_provider_contract_invalid", exception.ErrorCode);
        Assert.Empty(composer.Segments);
    }

    [Fact]
    public async Task Preview_only_enablement_rejects_formal_narration_before_HTTP()
    {
        var client = new RecordingClient();
        var provider = new BlueMagpieMultiVoiceNarrationProvider(
            client,
            new EphemeralChunkCache(),
            new RecordingComposer(),
            Options.Create(new BlueMagpieOptions
            {
                Enabled = true,
                FormalNarrationEnabled = false,
                InternalToken = new string('t', 32),
                ModelRevision = BlueMagpieOptions.PinnedModelRevision,
            }),
            NullLogger<BlueMagpieMultiVoiceNarrationProvider>.Instance,
            _metricsServices.GetRequiredService<BlueMagpieNarrationMetrics>());

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            provider.SynthesizeAsync(
                CreateRequest([NeutralTurn()]),
                "unused.mp3",
                null,
                CancellationToken.None));

        Assert.Equal("provider_version_mismatch", exception.ErrorCode);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Invalid_returned_model_contract_is_permanent_and_does_not_compose()
    {
        var root = CreateRoot();
        var client = new RecordingClient { ReturnedRevision = new string('0', 40) };
        var composer = new RecordingComposer();
        var provider = CreateProvider(client, composer);

        try
        {
            var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
                provider.SynthesizeAsync(
                    CreateRequest([NeutralTurn()]),
                    Path.Combine(root, "unused.mp3"),
                    null,
                    CancellationToken.None));

            Assert.Equal("bluemagpie_provider_contract_invalid", exception.ErrorCode);
            Assert.Empty(composer.Segments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_returned_provider_contract_is_permanent_and_does_not_compose()
    {
        var root = CreateRoot();
        var client = new RecordingClient { ReturnedProviderVersion = BlueMagpieOptions.PinnedModelRevision };
        var composer = new RecordingComposer();
        var provider = CreateProvider(client, composer);

        try
        {
            var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
                provider.SynthesizeAsync(
                    CreateRequest([NeutralTurn()]),
                    Path.Combine(root, "unused.mp3"),
                    null,
                    CancellationToken.None));

            Assert.Equal("bluemagpie_provider_contract_invalid", exception.ErrorCode);
            Assert.Empty(composer.Segments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_stops_the_current_chunk_and_removes_working_files()
    {
        var root = CreateRoot();
        var outputPath = Path.Combine(root, "book.mp3");
        var client = new BlockingClient();
        var provider = CreateProvider(client, new RecordingComposer());
        using var cancellation = new CancellationTokenSource();

        try
        {
            var synthesis = provider.SynthesizeAsync(
                CreateRequest([NeutralTurn()]),
                outputPath,
                null,
                cancellation.Token);
            await client.Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synthesis);
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateDirectories(root, "bluemagpie-tts-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Retry_after_third_chunk_failure_uses_two_durable_hits_and_only_requests_the_missing_chunk()
    {
        var root = CreateRoot();
        var cacheRoot = Path.Combine(root, "cache");
        var request = CreateRequest(
        [
            NeutralTurn() with { Text = "第一段" },
            NeutralTurn() with { Text = "第二段" },
            NeutralTurn() with { Text = "第三段" },
        ]);
        try
        {
            var firstClient = new FailOnCallClient(3);
            var firstProvider = CreateProvider(
                firstClient,
                new RecordingComposer(),
                CreatePersistentCache(cacheRoot));
            var firstException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                firstProvider.SynthesizeAsync(
                    request,
                    Path.Combine(root, "first.mp3"),
                    null,
                    TestContext.Current.CancellationToken));
            Assert.Equal("bluemagpie_provider_unavailable", firstException.Message);
            Assert.Equal(3, firstClient.Requests.Count);

            var resumedClient = new RecordingClient();
            var resumedComposer = new RecordingComposer();
            var resumedProvider = CreateProvider(
                resumedClient,
                resumedComposer,
                CreatePersistentCache(cacheRoot));
            await resumedProvider.SynthesizeAsync(
                request,
                Path.Combine(root, "resumed.mp3"),
                null,
                TestContext.Current.CancellationToken);

            var onlyRequest = Assert.Single(resumedClient.Requests);
            Assert.Equal("第三段", onlyRequest.Text);
            Assert.Equal(3, resumedComposer.Segments.Count);
            Assert.All(resumedComposer.Segments, segment => Assert.True(File.Exists(segment.InputWavPath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Composer_failure_preserves_all_chunks_and_retry_makes_zero_HTTP_requests()
    {
        var root = CreateRoot();
        var cacheRoot = Path.Combine(root, "cache");
        var request = CreateRequest(
        [
            NeutralTurn() with { Text = "第一段" },
            NeutralTurn() with { Text = "第二段" },
        ]);
        try
        {
            var firstClient = new RecordingClient();
            var firstProvider = CreateProvider(
                firstClient,
                new ThrowingComposer(),
                CreatePersistentCache(cacheRoot));
            var firstException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                firstProvider.SynthesizeAsync(
                    request,
                    Path.Combine(root, "failed-compose.mp3"),
                    null,
                    TestContext.Current.CancellationToken));
            Assert.Equal("bluemagpie_provider_failed", firstException.Message);
            Assert.Equal(2, firstClient.Requests.Count);

            var resumedClient = new RecordingClient();
            var resumedComposer = new RecordingComposer();
            var resumedProvider = CreateProvider(
                resumedClient,
                resumedComposer,
                CreatePersistentCache(cacheRoot));
            await resumedProvider.SynthesizeAsync(
                request,
                Path.Combine(root, "complete.mp3"),
                null,
                TestContext.Current.CancellationToken);

            Assert.Empty(resumedClient.Requests);
            Assert.Equal(2, resumedComposer.Segments.Count);
            Assert.All(resumedComposer.Segments, segment => Assert.True(File.Exists(segment.InputWavPath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_cache_context_fails_closed_before_HTTP()
    {
        var client = new RecordingClient();
        var provider = CreateProvider(client, new RecordingComposer());
        var request = CreateRequest([NeutralTurn()]) with { CacheContext = null };

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            provider.SynthesizeAsync(
                request,
                "unused.mp3",
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal("bluemagpie_provider_contract_invalid", exception.ErrorCode);
        Assert.Empty(client.Requests);
    }

    private BlueMagpieMultiVoiceNarrationProvider CreateProvider(
        IBlueMagpieTtsClient client,
        IFfmpegAudioComposer composer,
        IBlueMagpieChunkCache? cache = null,
        BlueMagpieOptions? providerOptions = null,
        ILogger<BlueMagpieMultiVoiceNarrationProvider>? logger = null)
    {
        var configured = providerOptions ?? new BlueMagpieOptions();
        configured.Enabled = true;
        configured.FormalNarrationEnabled = true;
        configured.InternalToken = new string('t', 32);
        configured.ModelRevision = BlueMagpieOptions.PinnedModelRevision;
        return new(
            client,
            cache ?? new EphemeralChunkCache(),
            composer,
            Options.Create(configured),
            logger ?? NullLogger<BlueMagpieMultiVoiceNarrationProvider>.Instance,
            _metricsServices.GetRequiredService<BlueMagpieNarrationMetrics>());
    }

    private static BlueMagpieChunkCache CreatePersistentCache(string root) =>
        new(
            Options.Create(new BlueMagpieChunkCacheOptions
            {
                RootPath = root,
                MaximumBytes = 64 * 1024,
                LowWatermarkBytes = 32 * 1024,
                MinimumFreeBytes = 0,
                RetentionHours = 168,
                CleanupIntervalMinutes = 30,
                TemporaryEntryRetentionMinutes = 60,
                LockRetryMilliseconds = 25,
            }),
            Options.Create(new BlueMagpieOptions { MaximumResponseBytes = 1024 }),
            NullLogger<BlueMagpieChunkCache>.Instance);

    private static MultiVoiceNarrationRequest CreateRequest(
        IReadOnlyList<NarrationTurn> turns,
        IReadOnlyList<NarrationProviderContract>? characterContracts = null) =>
        new(
            turns,
            new NarrationProviderContract(
                BlueMagpieOptions.ProviderName,
                BlueMagpieMultiVoiceNarrationProvider.PinnedProviderVersion),
            characterContracts ??
            [
                new NarrationProviderContract(
                    BlueMagpieOptions.ProviderName,
                    BlueMagpieMultiVoiceNarrationProvider.PinnedProviderVersion),
            ],
            CreateCacheContext());

    private static NarrationSynthesisCacheContext CreateCacheContext() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', 64),
            Guid.NewGuid(),
            new string('b', 64),
            new string('c', 64),
            "bluemagpie-pcm16-concat-v1",
            "wav-48khz-mono-to-mp3-concat-v1");

    private static NarrationTurn NeutralTurn() =>
        new(
            "你好",
            BlueMagpieOptions.FemaleVoice,
            "+0%",
            "+0Hz",
            "+0%",
            0);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storyvoice-bluemagpie-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RecordingClient : IBlueMagpieTtsClient
    {
        public List<(string Text, string Voice)> Requests { get; } = [];
        public Exception? Failure { get; init; }
        public string ReturnedRevision { get; init; } = BlueMagpieOptions.PinnedModelRevision;
        public string ReturnedProviderVersion { get; init; } = BlueMagpieOptions.PinnedProviderVersion;

        public Task<BlueMagpieSynthesisResult> SynthesizeAsync(
            string text,
            string voice,
            CancellationToken cancellationToken)
        {
            Requests.Add((text, voice));
            if (Failure is not null)
            {
                return Task.FromException<BlueMagpieSynthesisResult>(Failure);
            }

            return Task.FromResult(new BlueMagpieSynthesisResult(
                CreateWavBytes(),
                "audio/wav",
                ReturnedRevision,
                ReturnedProviderVersion,
                voice));
        }
    }

    private sealed class BlockingClient : IBlueMagpieTtsClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BlueMagpieSynthesisResult> SynthesizeAsync(
            string text,
            string voice,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking test client unexpectedly resumed.");
        }
    }

    private sealed class FailOnCallClient(int failingCall) : IBlueMagpieTtsClient
    {
        public List<(string Text, string Voice)> Requests { get; } = [];

        public Task<BlueMagpieSynthesisResult> SynthesizeAsync(
            string text,
            string voice,
            CancellationToken cancellationToken)
        {
            Requests.Add((text, voice));
            if (Requests.Count == failingCall)
            {
                return Task.FromException<BlueMagpieSynthesisResult>(
                    new SeriesVoicePreviewUnavailableException());
            }

            return Task.FromResult(new BlueMagpieSynthesisResult(
                CreateWavBytes((short)Requests.Count),
                "audio/wav",
                BlueMagpieOptions.PinnedModelRevision,
                BlueMagpieOptions.PinnedProviderVersion,
                voice));
        }
    }

    private sealed class RecordingComposer : IFfmpegAudioComposer
    {
        public IReadOnlyList<FfmpegAudioSegment> Segments { get; private set; } = [];
        public int OutputSampleRate { get; private set; }
        public MultiVoiceSynthesisResult Result { get; } = new([new(0, 250, 2000), new(1, 2750, 1000)]);

        public async Task<MultiVoiceSynthesisResult> ComposeAsync(
            IReadOnlyList<FfmpegAudioSegment> segments,
            string outputPath,
            int outputSampleRate,
            CancellationToken cancellationToken)
        {
            Segments = segments.ToArray();
            OutputSampleRate = outputSampleRate;
            Assert.All(Segments, segment => Assert.True(File.Exists(segment.InputWavPath)));
            Assert.All(Segments, segment => Assert.False(segment.DeleteInputAfterNormalization));
            await File.WriteAllBytesAsync(
                outputPath,
                Encoding.ASCII.GetBytes("ID3-mock"),
                cancellationToken);
            return Result;
        }
    }

    private sealed class ThrowingComposer : IFfmpegAudioComposer
    {
        public Task<MultiVoiceSynthesisResult> ComposeAsync(
            IReadOnlyList<FfmpegAudioSegment> segments,
            string outputPath,
            int outputSampleRate,
            CancellationToken cancellationToken) =>
            Task.FromException<MultiVoiceSynthesisResult>(new InvalidOperationException("synthetic composer failure"));
    }

    private sealed class EphemeralChunkCache : IBlueMagpieChunkCache
    {
        public Task<IBlueMagpieChunkCacheScope> OpenScopeAsync(
            NarrationSynthesisCacheContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult<IBlueMagpieChunkCacheScope>(new EphemeralScope());

        public Task CleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private sealed class EphemeralScope : IBlueMagpieChunkCacheScope
        {
            private readonly string _root = CreateRoot();

            public async Task<BlueMagpieChunkCacheEntry> GetOrCreateAsync(
                BlueMagpieChunkCacheRequest request,
                Func<CancellationToken, Task<byte[]>> createAudio,
                CancellationToken cancellationToken)
            {
                var audio = await createAudio(cancellationToken);
                var path = Path.Combine(_root, $"{request.Ordinal:00000}.wav");
                await File.WriteAllBytesAsync(path, audio, cancellationToken);
                return new BlueMagpieChunkCacheEntry(path, audio.LongLength, CacheHit: false);
            }

            public ValueTask DisposeAsync()
            {
                Directory.Delete(_root, recursive: true);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ReportedSizeChunkCache(long reportedAudioBytes) : IBlueMagpieChunkCache
    {
        public Task<IBlueMagpieChunkCacheScope> OpenScopeAsync(
            NarrationSynthesisCacheContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult<IBlueMagpieChunkCacheScope>(new ReportedSizeScope(reportedAudioBytes));

        public Task CleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private sealed class ReportedSizeScope : IBlueMagpieChunkCacheScope
        {
            private readonly long _reportedAudioBytes;
            private readonly string _root = CreateRoot();

            internal ReportedSizeScope(long reportedAudioBytes)
            {
                _reportedAudioBytes = reportedAudioBytes;
            }

            public async Task<BlueMagpieChunkCacheEntry> GetOrCreateAsync(
                BlueMagpieChunkCacheRequest request,
                Func<CancellationToken, Task<byte[]>> createAudio,
                CancellationToken cancellationToken)
            {
                var path = Path.Combine(_root, $"{request.Ordinal:00000}.wav");
                await File.WriteAllBytesAsync(path, CreateWavBytes(), cancellationToken);
                return new BlueMagpieChunkCacheEntry(path, _reportedAudioBytes, CacheHit: true);
            }

            public ValueTask DisposeAsync()
            {
                Directory.Delete(_root, recursive: true);
                return ValueTask.CompletedTask;
            }
        }
    }

    private static byte[] CreateWavBytes(short sample = 0)
    {
        var bytes = new byte[46];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 38);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 48_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 96_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44, 2), sample);
        return bytes;
    }
}

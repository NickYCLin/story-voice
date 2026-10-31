using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class VoAiMultiVoiceNarrationProviderTests
{
    [Fact]
    public void ProviderName_is_voai_and_registry_resolves_it_case_insensitively()
    {
        var provider = CreateProvider(new FakeVoAiTtsClient(), new CapturingAudioComposer());
        var registry = new NarrationProviderRegistry([provider]);

        Assert.Equal("voai", provider.ProviderName);
        Assert.Same(provider, registry.Resolve("VOAI"));
    }

    [Fact]
    public async Task SynthesizeAsync_splits_at_1000_and_preserves_the_versioned_voice_contract()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storyvoice-voai-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var outputPath = Path.Combine(root, "book.mp3.tmp-test");
        var client = new FakeVoAiTtsClient();
        var composer = new CapturingAudioComposer();
        var provider = CreateProvider(client, composer);
        var progress = new List<NarrationSynthesisProgress>();
        var request = new MultiVoiceNarrationRequest(
        [
            new NarrationTurn(
                new string('甲', 1_001),
                "v1:Neo:佑希:預設",
                "+10%",
                "+20Hz",
                "-5%",
                250)
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

            Assert.Equal(2, client.Requests.Count);
            Assert.Same(composer.Result, result);
            Assert.All(composer.Segments, segment => Assert.Equal(0, segment.TurnIndex));
            Assert.Equal(1_000, client.Requests[0].Text.Length);
            Assert.Single(client.Requests[1].Text);
            Assert.All(client.Requests, item =>
            {
                Assert.Equal("Neo", item.Model);
                Assert.Equal("佑希", item.Speaker);
                Assert.Equal("預設", item.Style);
                Assert.Equal(1.1, item.Speed, precision: 10);
                Assert.Equal(4, item.PitchShift);
            });

            Assert.Equal(2, composer.Segments.Count);
            Assert.Equal(250, composer.Segments[0].PauseBeforeMs);
            Assert.Equal(0, composer.Segments[1].PauseBeforeMs);
            Assert.All(composer.Segments, item => Assert.Equal("-5%", item.Volume));
            Assert.Collection(
                progress,
                item => Assert.Equal(new NarrationSynthesisProgress(1, 2), item),
                item => Assert.Equal(new NarrationSynthesisProgress(2, 2), item));
            Assert.True(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateDirectories(root, "voai-tts-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("v2:Neo:佑希:預設")]
    [InlineData("v1:Neo:佑希")]
    [InlineData("v1::佑希:預設")]
    [InlineData("Neo:佑希:預設")]
    public async Task SynthesizeAsync_rejects_an_unpinned_voice_reference_before_calling_voai(string voice)
    {
        var client = new FakeVoAiTtsClient();
        var composer = new CapturingAudioComposer();
        var provider = CreateProvider(client, composer);
        var request = new MultiVoiceNarrationRequest(
            [new NarrationTurn("text", voice, "+0%", "+0Hz", "+0%", 0)]);

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            provider.SynthesizeAsync(request, "unused.mp3", null, CancellationToken.None));

        Assert.Equal("voai_provider_failed", exception.ErrorCode);
        Assert.Empty(client.Requests);
        Assert.Empty(composer.Segments);
    }

    [Fact]
    public async Task SynthesizeAsync_rejects_invalid_volume_before_calling_voai()
    {
        var client = new FakeVoAiTtsClient();
        var composer = new CapturingAudioComposer();
        var provider = CreateProvider(client, composer);
        var request = new MultiVoiceNarrationRequest(
            [new NarrationTurn("text", "v1:Neo:佑希:預設", "+0%", "+0Hz", "loud", 0)]);

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            provider.SynthesizeAsync(request, "unused.mp3", null, CancellationToken.None));

        Assert.Equal("voai_provider_failed", exception.ErrorCode);
        Assert.Empty(client.Requests);
        Assert.Empty(composer.Segments);
    }

    [Fact]
    public async Task SynthesizeAsync_rejects_a_job_over_the_chunk_budget_before_calling_voai()
    {
        var client = new FakeVoAiTtsClient();
        var composer = new CapturingAudioComposer();
        var provider = CreateProvider(client, composer, maximumChunksPerJob: 1);
        var request = new MultiVoiceNarrationRequest(
            [new NarrationTurn(new string('甲', 1_001), "v1:Neo:佑希:預設", "+0%", "+0Hz", "+0%", 0)]);

        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
            provider.SynthesizeAsync(request, "unused.mp3", null, CancellationToken.None));

        Assert.Equal("voai_provider_failed", exception.ErrorCode);
        Assert.Empty(client.Requests);
        Assert.Empty(composer.Segments);
    }

    [Theory]
    [InlineData("+0%", 1.0)]
    [InlineData("+10%", 1.1)]
    [InlineData("-75%", 0.5)]
    [InlineData("+80%", 1.5)]
    public void MapRateToSpeed_converts_percent_and_clamps_to_voai_bounds(string rate, double expected)
    {
        Assert.Equal(expected, VoAiMultiVoiceNarrationProvider.MapRateToSpeed(rate), precision: 10);
    }

    [Theory]
    [InlineData("+20Hz", 4)]
    [InlineData("-20Hz", -4)]
    [InlineData("+3Hz", 1)]
    [InlineData("+100Hz", 5)]
    [InlineData("-100Hz", -5)]
    public void MapPitchToShift_rounds_each_five_hertz_and_clamps(string pitch, int expected)
    {
        Assert.Equal(expected, VoAiMultiVoiceNarrationProvider.MapPitchToShift(pitch));
    }

    [Theory]
    [InlineData("10", true)]
    [InlineData("fast", true)]
    [InlineData("20", false)]
    [InlineData("high", false)]
    public void Parameter_mapping_requires_the_versioned_units(string value, bool isRate)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            if (isRate)
            {
                _ = VoAiMultiVoiceNarrationProvider.MapRateToSpeed(value);
            }
            else
            {
                _ = VoAiMultiVoiceNarrationProvider.MapPitchToShift(value);
            }
        });
    }

    [Theory]
    [InlineData("+25%", 1.25)]
    [InlineData("-20%", 0.8)]
    [InlineData("-150%", 0)]
    [InlineData("+500%", 2)]
    public void Composer_maps_edge_volume_percent_to_an_ffmpeg_factor(string volume, double expected)
    {
        Assert.Equal(expected, FfmpegVoAiAudioComposer.ParseVolumeFactor(volume), precision: 10);
    }

    [Fact]
    public void Composer_rejects_volume_without_the_percent_unit()
    {
        Assert.Throws<ArgumentException>(() => FfmpegVoAiAudioComposer.ParseVolumeFactor("25"));
    }

    [Fact]
    public async Task SynthesizeAsync_cleans_wav_working_files_when_composition_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storyvoice-voai-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var provider = CreateProvider(new FakeVoAiTtsClient(), new ThrowingAudioComposer());
        var request = new MultiVoiceNarrationRequest(
            [new NarrationTurn("text", "v1:Neo:佑希:預設", "+0%", "+0Hz", "+0%", 0)]);

        try
        {
            var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() =>
                provider.SynthesizeAsync(
                    request,
                    Path.Combine(root, "book.mp3.tmp-test"),
                    null,
                    CancellationToken.None));

            Assert.Equal("voai_provider_failed", exception.ErrorCode);
            Assert.DoesNotContain("composition failed", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateDirectories(root, "voai-tts-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static VoAiMultiVoiceNarrationProvider CreateProvider(
        IVoAiTtsClient client,
        IVoAiAudioComposer composer,
        int maximumChunksPerJob = 5_000) =>
        new(
            client,
            composer,
            Options.Create(new VoAiOptions
            {
                MaximumChunksPerJob = maximumChunksPerJob,
                MaximumJobResponseBytes = 4L * 1024 * 1024 * 1024,
            }),
            NullLogger<VoAiMultiVoiceNarrationProvider>.Instance);

    private sealed class FakeVoAiTtsClient : IVoAiTtsClient
    {
        public List<VoAiSpeechSynthesisRequest> Requests { get; } = [];

        public async Task SynthesizeWavAsync(
            VoAiSpeechSynthesisRequest request,
            Stream destination,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var wav = new byte[44];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
            await destination.WriteAsync(wav, cancellationToken);
        }
    }

    private sealed class CapturingAudioComposer : IVoAiAudioComposer
    {
        public IReadOnlyList<VoAiAudioSegment> Segments { get; private set; } = [];
        public MultiVoiceSynthesisResult Result { get; } = new([new(0, 250, 2000)]);

        public async Task<MultiVoiceSynthesisResult> ComposeAsync(
            IReadOnlyList<VoAiAudioSegment> segments,
            string outputPath,
            CancellationToken cancellationToken)
        {
            Segments = segments.ToArray();
            await File.WriteAllBytesAsync(outputPath, Encoding.ASCII.GetBytes("ID3-mock"), cancellationToken);
            return Result;
        }
    }

    private sealed class ThrowingAudioComposer : IVoAiAudioComposer
    {
        public Task<MultiVoiceSynthesisResult> ComposeAsync(
            IReadOnlyList<VoAiAudioSegment> segments,
            string outputPath,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("composition failed");
    }
}

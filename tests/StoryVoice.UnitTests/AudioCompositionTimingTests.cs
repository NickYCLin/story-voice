using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

// These tests use generated tones and real ffmpeg/ffprobe; no model, paid API or private audio.
public sealed class AudioCompositionTimingTests
{
    [Fact]
    public async Task Wav_composition_measures_resampled_chunks_and_preserves_cached_inputs()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            var first = Path.Combine(root, "first.wav");
            var second = Path.Combine(root, "second.wav");
            var third = Path.Combine(root, "third.wav");
            WriteTone(first, 1);
            WriteTone(second, 0.5);
            WriteTone(third, 2);
            var output = Path.Combine(root, "finished.mp3");
            IFfmpegAudioComposer composer = new FfmpegVoAiAudioComposer(
                Options.Create(new VoAiOptions()), NullLogger<FfmpegVoAiAudioComposer>.Instance);
            var result = await composer.ComposeAsync([
                new(first, "+0%", 250, DeleteInputAfterNormalization: false, TurnIndex: 0),
                new(second, "-5%", 0, TurnIndex: 0),
                new(third, "+0%", 500, TurnIndex: 1),
            ], output, 48_000, ct);

            Assert.Equal(new[] { new NarrationTurnTiming(0, 250, 1500), new(1, 2250, 2000) }, result.TurnTimings);
            Assert.True(File.Exists(first));
            Assert.False(File.Exists(second));
            Assert.False(File.Exists(third));
            Assert.InRange(await ProbeSecondsAsync(output, ct), 4.25, 4.35);
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Threewa_measures_encoded_silence_and_chunk_durations_before_atomic_publication()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateRoot();
        try
        {
            var wav = Path.Combine(root, "synthetic.wav");
            WriteTone(wav, 1);
            var client = new ToneClient(await File.ReadAllBytesAsync(wav, ct));
            var provider = new ThreeWaVoxCpm2NarrationProvider(client, NullLogger<ThreeWaVoxCpm2NarrationProvider>.Instance);
            var output = Path.Combine(root, "finished.mp3");
            var result = await provider.SynthesizeAsync(new([
                new(new string('甲', 4001), "clone:synthetic-one", "+0%", "+0Hz", "+0%", 250),
                new("合成測試。", "clone:synthetic-two", "+0%", "+0Hz", "+0%", 500),
            ]), output, null, ct);

            Assert.Equal(3, client.Submissions);
            Assert.NotNull(result.TurnTimings);
            Assert.Equal(2, result.TurnTimings.Count);
            var first = result.TurnTimings[0];
            var second = result.TurnTimings[1];
            Assert.InRange(first.StartMs, 250, 310);
            Assert.InRange(first.DurationMs, 2000, 2150);
            Assert.InRange(second.StartMs - first.StartMs - first.DurationMs, 500, 570);
            Assert.InRange(second.DurationMs, 1000, 1100);
            var endMs = second.StartMs + second.DurationMs;
            Assert.InRange(Math.Abs(await ProbeSecondsAsync(output, ct) * 1000 - endMs), 0, 100);
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storyvoice audio 'proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteTone(string path, double seconds)
    {
        const int sampleRate = 44_100;
        var samples = (int)(seconds * sampleRate);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples * 2);
        for (var sample = 0; sample < samples; sample++)
            writer.Write((short)(3000 * Math.Sin(2 * Math.PI * 440 * sample / sampleRate)));
    }

    private static async Task<double> ProbeSecondsAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.Equal(0, process.ExitCode);
        return double.Parse(output.Trim(), CultureInfo.InvariantCulture);
    }

    private sealed class ToneClient(byte[] wav) : IThreeWaSynthesisClient
    {
        public int Submissions { get; private set; }
        public Task<ThreeWaSynthesisTaskHandle> SubmitAsync(ThreeWaSynthesisRequest request, CancellationToken cancellationToken)
        {
            Submissions++;
            return Task.FromResult(new ThreeWaSynthesisTaskHandle("synthetic", "status", "result", "artifact"));
        }
        public Task<string> GetTaskStatusAsync(string statusUrl, CancellationToken cancellationToken) => Task.FromResult("success");
        public Task<IReadOnlyList<ThreeWaSynthesisArtifact>> GetResultArtifactsAsync(string resultUrl, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ThreeWaSynthesisArtifact>>([new("synthetic", "audio/wav")]);
        public async Task DownloadArtifactAsync(string artifactUrlTemplate, string artifactId, Stream destination, CancellationToken cancellationToken) =>
            await destination.WriteAsync(wav, cancellationToken);
    }
}

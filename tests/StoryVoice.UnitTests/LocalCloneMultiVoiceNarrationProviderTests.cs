using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Narrations;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class LocalCloneMultiVoiceNarrationProviderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"clone-integrity-{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory]
    [InlineData("English 123", true)]
    [InlineData("123", true)]
    [InlineData("🙂", true)]
    [InlineData("中文字", true)]
    [InlineData(" \t。！？…\n", false)]
    public void Only_punctuation_and_whitespace_may_be_omitted(string text, bool expected) =>
        Assert.Equal(expected, LocalCloneMultiVoiceNarrationProvider.HasSpeakableText(text));

    [Theory]
    [InlineData("zh-TW-YunJheNeural")]
    [InlineData("local-clone-narrator")]
    public async Task Unmanaged_narrator_never_falls_back_to_an_available_character(string voice)
    {
        var client = new FakeClient(_ => Wave());
        var request = Request("必須明確選擇旁白。");
        request = request with { Turns = request.Turns.Select(t => t with { Voice = voice }).ToArray() };
        var error = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() => Provider(client)
            .SynthesizeAsync(request, Path.Combine(root, "blocked.mp3"), null, TestContext.Current.CancellationToken));
        Assert.Equal(LocalCloneMultiVoiceNarrationProvider.MissingReferenceCode, error.ErrorCode);
        Assert.Empty(client.Texts);
    }

    [Fact]
    public void Chunking_preserves_every_utf16_unit_and_surrogate_pair()
    {
        var text = "  " + new string('a', 156) + "🙂 中文，" + new string('b', 170) + "\n ";
        var chunks = LocalCloneMultiVoiceNarrationProvider.SplitText(text, 160);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk =>
        {
            Assert.InRange(chunk.Length, 1, 160);
            Assert.False(char.IsHighSurrogate(chunk[^1]));
            Assert.False(char.IsLowSurrogate(chunk[0]));
        });
    }

    [Fact]
    public async Task Disabled_formal_provider_never_calls_gateway()
    {
        var client = new FakeClient(_ => Wave());
        await Assert.ThrowsAsync<PermanentNarrationProviderException>(() => Provider(client, false)
            .SynthesizeAsync(Request("hello"), Path.Combine(root, "out.mp3"), null, TestContext.Current.CancellationToken));
        Assert.Empty(client.Texts);
    }

    [Fact]
    public async Task Mixed_language_chunks_produce_hash_bound_receipt_without_private_text()
    {
        var client = new FakeClient(_ => Wave());
        var output = Path.Combine(root, "out.mp3");
        await Provider(client).SynthesizeAsync(Request("English 123 中文🙂"), output, null, TestContext.Current.CancellationToken);
        Assert.Contains("English 123 中文🙂", client.Texts);
        Assert.True(new FileInfo(output).Length > 0);
        var receiptText = await File.ReadAllTextAsync(output + ".integrity.json", TestContext.Current.CancellationToken);
        using var receipt = JsonDocument.Parse(receiptText);
        Assert.Equal("complete", receipt.RootElement.GetProperty("Status").GetString());
        Assert.Equal(Hash(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken)), receipt.RootElement.GetProperty("AudioSha256").GetString());
        Assert.DoesNotContain("English", receiptText);
    }

    [Fact]
    public async Task Failed_fallback_sentence_cannot_publish_partial_audio()
    {
        var client = new FakeClient(text => text == "成功。" ? Wave() : throw Unavailable());
        var output = Path.Combine(root, "out.mp3");
        var exception = await Assert.ThrowsAsync<PermanentNarrationProviderException>(() => Provider(client)
            .SynthesizeAsync(Request("成功。失敗。"), output, null, TestContext.Current.CancellationToken));
        Assert.Equal("local_clone_incomplete_audio", exception.ErrorCode);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".integrity.json"));
        var receipt = Assert.Single(Directory.GetFiles(root, "integrity.json", SearchOption.AllDirectories));
        Assert.Contains("incomplete", await File.ReadAllTextAsync(receipt, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Altered_reference_fails_before_gateway()
    {
        var client = new FakeClient(_ => Wave());
        var request = Request("hello");
        await File.WriteAllBytesAsync(request.CloneReferences!.Values.Single().ReferenceAudioPath, [1, 2, 3], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PermanentNarrationProviderException>(() => Provider(client)
            .SynthesizeAsync(request, Path.Combine(root, "out.mp3"), null, TestContext.Current.CancellationToken));
        Assert.Empty(client.Texts);
    }

    private LocalCloneMultiVoiceNarrationProvider Provider(FakeClient client, bool enabled = true) => new(client,
        Options.Create(new LocalClonePreviewOptions { FormalNarrationEnabled = enabled, RetryDelaySeconds = 0 }),
        Options.Create(new NarrationOptions { AudioRootPath = root }),
        NullLogger<LocalCloneMultiVoiceNarrationProvider>.Instance);

    [Fact]
    public async Task Measured_chunks_and_encoded_pauses_compose_a_chapter_and_character_timeline()
    {
        var client = new FakeClient(_ => Wave());
        var request = Request(new string('甲', 321));
        var voice = request.Turns[0].Voice;
        request = request with { Turns = [request.Turns[0] with { PauseBeforeMs = 250 }, new("English 123", voice, "+0%", "+0Hz", "+0%", 500)] };
        var output = Path.Combine(root, "out.mp3");
        var result = await Provider(client).SynthesizeAsync(request, output, null, TestContext.Current.CancellationToken);

        Assert.Equal(3, client.Texts.Count);
        Assert.NotNull(result.TurnTimings);
        Assert.Equal(2, result.TurnTimings.Count);
        var first = result.TurnTimings[0];
        var second = result.TurnTimings[1];
        Assert.Equal(0, first.TurnIndex);
        Assert.Equal(1, second.TurnIndex);
        Assert.InRange(first.StartMs, 250, 310);
        Assert.InRange(first.DurationMs, 3000, 3300);
        Assert.InRange(second.StartMs - first.StartMs - first.DurationMs, 500, 570);
        Assert.InRange(second.DurationMs, 1000, 1100);
        await AssertAudioEndAsync(output, second);

        var chapterId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var timeline = NarrationTimelineComposer.Compose([
            new(chapterId, 0, true, [new(SpeechSegmentSourceKind.Body, 0, 321, SpeechSegmentTurnKind.Narrator, null)]),
            new(chapterId, 0, false, [new(SpeechSegmentSourceKind.Body, 321, 11, SpeechSegmentTurnKind.Dialogue, characterId)]),
        ], result.TurnTimings);
        Assert.NotNull(timeline);
        Assert.Equal(chapterId, timeline.Turns[0].ChapterId);
        Assert.True(timeline.Turns[0].ChapterStart);
        Assert.Equal(characterId, timeline.Turns[1].Slices[0].CharacterId);
        Assert.Equal(second.StartMs, timeline.Turns[1].StartMs);
    }

    [Fact]
    public async Task Retried_job_measures_reused_cached_chunks_without_synthesizing_them_again()
    {
        var request = Request(new string('甲', 160) + new string('乙', 160) + "丙");
        var client = new FakeClient(_ => Wave());
        var provider = Provider(client);
        var output = Path.Combine(root, "out.mp3");
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SynthesizeAsync(request, output,
            (_, _) => throw new InvalidOperationException("Interrupted after caching the first chunk."), TestContext.Current.CancellationToken));
        Assert.Single(client.Texts);
        Assert.False(File.Exists(output));

        var result = await provider.SynthesizeAsync(request, output, null, TestContext.Current.CancellationToken);
        Assert.Equal(3, client.Texts.Count);
        var timing = Assert.Single(result.TurnTimings!);
        Assert.Equal(0, timing.StartMs);
        Assert.InRange(timing.DurationMs, 3000, 3300);
        await AssertAudioEndAsync(output, timing);
    }

    [Fact]
    public async Task Sentence_fallback_timing_uses_the_joined_audio_duration()
    {
        var client = new FakeClient(text => text == "甲。乙。" ? throw Unavailable() : Wave());
        var output = Path.Combine(root, "out.mp3");
        var result = await Provider(client).SynthesizeAsync(Request("甲。乙。"), output, null, TestContext.Current.CancellationToken);
        var timing = Assert.Single(result.TurnTimings!);
        Assert.Equal(4, client.Texts.Count);
        Assert.InRange(timing.DurationMs, 2000, 2200);
        await AssertAudioEndAsync(output, timing);
    }

    [Fact]
    public async Task Punctuation_only_chunks_do_not_shift_timing_but_empty_turns_disable_the_timeline()
    {
        var client = new FakeClient(_ => Wave());
        var request = Request(new string('。', 160) + "hello");
        var output = Path.Combine(root, "out.mp3");
        var result = await Provider(client).SynthesizeAsync(request, output, null, TestContext.Current.CancellationToken);
        var timing = Assert.Single(result.TurnTimings!);
        Assert.Equal(0, timing.StartMs);
        await AssertAudioEndAsync(output, timing);
        request = request with { Turns = [request.Turns[0] with { Text = "。", PauseBeforeMs = 250 }, request.Turns[0]] };
        var withoutTimeline = await Provider(client).SynthesizeAsync(request, Path.Combine(root, "no-timeline.mp3"), null, TestContext.Current.CancellationToken);
        Assert.Null(withoutTimeline.TurnTimings);
    }

    private static async Task AssertAudioEndAsync(string output, NarrationTurnTiming lastTurn)
    {
        var start = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var duration = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        Assert.InRange(Math.Abs(double.Parse(duration.Trim(), CultureInfo.InvariantCulture) * 1000 - lastTurn.StartMs - lastTurn.DurationMs), 0, 100);
    }

    private MultiVoiceNarrationRequest Request(string text)
    {
        Directory.CreateDirectory(root);
        var bytes = Wave();
        var path = Path.Combine(root, "reference.wav");
        File.WriteAllBytes(path, bytes);
        var key = MultiCharacterTurnBuilder.EncodeLocalCloneReference(Guid.NewGuid());
        return new([new(text, key, "+0%", "+0Hz", "+0%", 0)], CloneReferences:
            new Dictionary<string, LocalCloneVoiceReference> { [key] = new(path, "fixture transcript", Hash(bytes)) });
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static LocalClonePreviewUnavailableException Unavailable() => new(LocalClonePreviewFailureKind.GatewayUnavailable);
    private sealed class FakeClient(Func<string, byte[]> synthesize) : ILocalCloneGatewayClient
    {
        public List<string> Texts { get; } = [];
        public Task<LocalCloneGatewayAudio> SynthesizeAsync(LocalCloneGatewayRequest request, CancellationToken cancellationToken)
        {
            Texts.Add(request.Text);
            return Task.FromResult(new LocalCloneGatewayAudio(synthesize(request.Text), "audio/wav"));
        }
    }
    private static byte[] Wave()
    {
        var result = new byte[44 + 96000];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        "WAVEfmt "u8.CopyTo(result.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), 96000);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(34), 16);
        "data"u8.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), 96000);
        return result;
    }
}

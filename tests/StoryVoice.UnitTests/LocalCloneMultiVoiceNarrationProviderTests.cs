using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Series;
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

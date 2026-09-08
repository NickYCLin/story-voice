using System.Reflection;
using System.Text;
using System.Text.Json;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class NarrationUnicodeChunkTests
{
    [Theory]
    [InlineData("voai", 0x1F600)]
    [InlineData("voai", 0x20BB7)]
    [InlineData("voai", 0x1D11E)]
    [InlineData("3wa", 0x1F600)]
    [InlineData("3wa", 0x20BB7)]
    [InlineData("3wa", 0x1D11E)]
    public void Supplementary_characters_survive_each_chunks_utf8_and_json_encoding(string provider, int codePoint)
    {
        var limit = provider == "voai" ? 1000 : 4000;
        var scalar = char.ConvertFromUtf32(codePoint);
        foreach (var prefixLength in new[] { limit - 2, limit - 1, limit })
        {
            var text = new string('甲', prefixLength) + scalar + "乙";
            var chunks = Split(provider, text);
            Assert.Equal(text, string.Concat(chunks));
            foreach (var chunk in chunks)
            {
                Assert.InRange(chunk.Length, 1, limit);
                Assert.Null(Record.Exception(() => new UTF8Encoding(false, true).GetBytes(chunk)));
                Assert.Equal(chunk, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(chunk)));
            }
        }
    }

    [Theory]
    [InlineData("voai")]
    [InlineData("3wa")]
    public void Preferred_punctuation_and_bmp_chunk_lengths_remain_unchanged(string provider)
    {
        var limit = provider == "voai" ? 1000 : 4000;
        var plain = Split(provider, new string('甲', limit + 1));
        Assert.Equal([limit, 1], plain.Select(chunk => chunk.Length));
        var text = new string('甲', limit / 2) + "。" + new string('乙', limit / 2 - 2) + char.ConvertFromUtf32(0x20BB7) + "丙";
        var chunks = Split(provider, text);
        Assert.EndsWith("。", chunks[0]);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, limit));
    }

    private static IReadOnlyList<string> Split(string provider, string text) => provider == "voai"
        ? VoAiMultiVoiceNarrationProvider.SplitText(text)
        : (IReadOnlyList<string>)typeof(ThreeWaVoxCpm2NarrationProvider)
            .GetMethod("SplitText", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [text, 4000])!;
}

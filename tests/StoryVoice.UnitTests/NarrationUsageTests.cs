using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class NarrationUsageTests
{
    [Theory]
    [InlineData("小雨😀", 3)]
    [InlineData("a\r\nb", 4)]
    [InlineData("", 0)]
    public void Input_count_uses_unicode_scalar_values_including_whitespace(string text, long expected)
    {
        Assert.Equal(expected, NarrationUsageRecorder.CountCharacters([text]));
        Assert.Equal(expected * 2, NarrationUsageRecorder.CountCharacters([text, text]));
    }
}

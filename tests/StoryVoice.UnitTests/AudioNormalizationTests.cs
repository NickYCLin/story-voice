using System.Globalization;
using StoryVoice.Worker;
using Xunit;

namespace StoryVoice.UnitTests;

public sealed class AudioNormalizationTests
{
    [Theory]
    [InlineData(-16.0, -1.5, 11.0, "loudnorm=I=-16:TP=-1.5:LRA=11")]
    [InlineData(-23.0, -1.0, 7.0, "loudnorm=I=-23:TP=-1:LRA=7")]
    [InlineData(-14.5, -2.0, 14.2, "loudnorm=I=-14.5:TP=-2:LRA=14.2")]
    public void BuildLoudnessNormalizationFilter_FormatsStandardParameters(
        double integratedLoudness,
        double truePeak,
        double loudnessRange,
        string expected)
    {
        var filter = FfmpegVoAiAudioComposer.BuildLoudnessNormalizationFilter(
            integratedLoudness,
            truePeak,
            loudnessRange);

        Assert.Equal(expected, filter);
    }

    [Theory]
    [InlineData(-75.0, -1.5, 11.0)]
    [InlineData(-4.0, -1.5, 11.0)]
    [InlineData(-16.0, -10.0, 11.0)]
    [InlineData(-16.0, 1.0, 11.0)]
    [InlineData(-16.0, -1.5, 0.5)]
    [InlineData(-16.0, -1.5, 55.0)]
    public void BuildLoudnessNormalizationFilter_RejectsOutOfRangeParameters(
        double integratedLoudness,
        double truePeak,
        double loudnessRange)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FfmpegVoAiAudioComposer.BuildLoudnessNormalizationFilter(
                integratedLoudness,
                truePeak,
                loudnessRange));
    }

    [Fact]
    public void AudioComposerOptions_HasSensibleDefaults()
    {
        var options = new AudioComposerOptions();

        Assert.True(options.EnableLoudnessNormalization);
        Assert.Equal(-16.0, options.TargetIntegratedLoudness);
        Assert.Equal(-1.5, options.TargetTruePeak);
        Assert.Equal(11.0, options.TargetLoudnessRange);
    }

    [Theory]
    [InlineData("+20%", 250, "volume=1.2,adelay=250:all=1")]
    [InlineData("-10%", 0, "volume=0.9")]
    public void BuildAudioFilter_CombinesVolumeAndPause(string volume, int pauseMs, string expected)
    {
        var filter = FfmpegVoAiAudioComposer.BuildAudioFilter(volume, pauseMs);

        Assert.Equal(expected, filter);
    }
}

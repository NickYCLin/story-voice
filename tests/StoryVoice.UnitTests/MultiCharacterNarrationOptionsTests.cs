using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.UnitTests;

public sealed class MultiCharacterNarrationOptionsTests
{
    [Theory]
    [InlineData("edge", "edge-tts-7.2.8", "edge-tts-multi-voice-v1", "concat-demuxer-mp3-v1")]
    [InlineData("3WA-VOXCPM2", "3wa-voxcpm2-api-v1", "3wa-voxcpm2-turn-concat-v2", "mixed-audio-to-mp3-concat-v1")]
    [InlineData("voai", "voai-voice-api-v1", "voai-speech-turn-concat-v1", "wav-32khz-to-mp3-concat-v1")]
    [InlineData("bluemagpie", "bm1-d2d7ef3e81456915eb7a3cfe2446a9f19417c21b", "bluemagpie-pcm16-concat-v1", "wav-48khz-mono-to-mp3-concat-v1")]
    public void Provider_metadata_is_resolved_per_synthesis_provider(
        string provider,
        string expectedProviderVersion,
        string expectedCompositionVersion,
        string expectedFfmpegProfile)
    {
        var options = new MultiCharacterNarrationOptions();

        Assert.Equal(expectedProviderVersion, options.ResolveProviderVersion(provider));
        Assert.Equal(expectedCompositionVersion, options.ResolveCompositionVersion(provider));
        Assert.Equal(expectedFfmpegProfile, options.ResolveFfmpegProfile(provider));
    }

    [Fact]
    public void Unknown_provider_uses_legacy_metadata_for_existing_configurations()
    {
        var options = new MultiCharacterNarrationOptions
        {
            ProviderVersion = "legacy-provider-v2",
            CompositionVersion = "legacy-composition-v3",
            FfmpegProfile = "legacy-ffmpeg-v4",
        };

        Assert.Equal("legacy-provider-v2", options.ResolveProviderVersion("synthetic-provider"));
        Assert.Equal("legacy-composition-v3", options.ResolveCompositionVersion("synthetic-provider"));
        Assert.Equal("legacy-ffmpeg-v4", options.ResolveFfmpegProfile("synthetic-provider"));
    }

    [Fact]
    public void Missing_provider_is_rejected()
    {
        var options = new MultiCharacterNarrationOptions();

        Assert.Throws<InvalidOperationException>(() => options.ResolveProviderVersion(" "));
    }
}

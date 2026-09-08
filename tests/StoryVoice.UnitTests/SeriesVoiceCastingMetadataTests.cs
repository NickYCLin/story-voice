using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StoryVoice.Infrastructure;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.UnitTests;

public sealed class SeriesVoiceCastingMetadataTests
{
    [Fact]
    public void Default_catalog_only_labels_the_existing_known_male_and_female_voices()
    {
        var voices = SeriesVoiceCatalogOptions.CreateDefaultVoices();
        Assert.All(voices, voice => Assert.True(voice.HasValidCastingMetadata()));
        Assert.All(voices.Where(voice => voice.Provider == "edge" || voice.Provider == "bluemagpie"),
            voice => Assert.Contains(voice.Gender, new[] { "male", "female" }));
        Assert.All(voices.Where(voice => voice.Provider == "voai" || voice.Provider == "3wa-voxcpm2"),
            voice => Assert.Null(voice.Gender));
        Assert.All(voices, voice =>
        {
            Assert.Null(voice.MinimumAge);
            Assert.Null(voice.MaximumAge);
            Assert.Empty(voice.CharacterTags);
        });
    }

    [Theory]
    [InlineData("Gender", "guessed")]
    [InlineData("MinimumAge", "-1")]
    [InlineData("MaximumAge", "151")]
    [InlineData("MinimumAge", "60")]
    [InlineData("CharacterTags:0", "")]
    [InlineData("CharacterTags:1", "calm")]
    public void Invalid_operator_metadata_is_rejected_before_catalog_use(string field, string value)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=synthetic;Username=synthetic;Password=synthetic",
            ["SeriesVoiceCatalog:Voices:0:Provider"] = "edge",
            ["SeriesVoiceCatalog:Voices:0:Voice"] = "synthetic-voice",
            ["SeriesVoiceCatalog:Voices:0:DisplayName"] = "測試聲線",
            ["SeriesVoiceCatalog:Voices:0:Locale"] = "zh-TW",
            ["SeriesVoiceCatalog:Voices:0:Gender"] = "female",
            ["SeriesVoiceCatalog:Voices:0:MinimumAge"] = "18",
            ["SeriesVoiceCatalog:Voices:0:MaximumAge"] = "40",
            ["SeriesVoiceCatalog:Voices:0:CharacterTags:0"] = "calm",
            [$"SeriesVoiceCatalog:Voices:0:{field}"] = value,
        };
        var services = new ServiceCollection();
        services.AddStoryVoiceInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SeriesVoiceCatalogOptions>>().Value);
    }

    [Fact]
    public void Explicit_age_and_style_metadata_is_supported_without_changing_voice_identity()
    {
        var voice = new SeriesVoiceCatalogEntry { Provider = "edge", Voice = "synthetic-voice", Gender = "female",
            MinimumAge = 18, MaximumAge = 40, CharacterTags = ["calm", "健談"] };
        Assert.True(voice.HasValidCastingMetadata());
        voice.CharacterTags = Enumerable.Range(0, 17).Select(index => $"tag-{index}").ToList();
        Assert.False(voice.HasValidCastingMetadata());
        voice.CharacterTags = [new string('x', 41)];
        Assert.False(voice.HasValidCastingMetadata());
    }
}

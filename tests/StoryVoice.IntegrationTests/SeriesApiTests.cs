using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryVoice.Application.Books;
using StoryVoice.Application.Series;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class SeriesApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Catalog_returns_operator_supplied_casting_metadata_for_synthetic_voices()
    {
        var ct = TestContext.Current.CancellationToken;
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<SeriesVoiceCatalogOptions>(options => options.Voices =
            [
                new SeriesVoiceCatalogEntry { Provider = "edge", Voice = "synthetic-voice", DisplayName = "測試聲線", Locale = "zh-TW",
                    Gender = "female", MinimumAge = 18, MaximumAge = 40, CharacterTags = ["calm", "健談"] },
            ])));
        using var owner = await configured.CreateAuthenticatedClientAsync(ct);
        var response = await owner.GetFromJsonAsync<SeriesVoiceOptionResponse[]>("/api/series/voice-options", ct);
        var voice = Assert.Single(response!);
        Assert.Equal("synthetic-voice", voice.Voice);
        Assert.Equal("female", voice.Gender);
        Assert.Equal(18, voice.MinimumAge);
        Assert.Equal(40, voice.MaximumAge);
        Assert.Equal(new[] { "calm", "健談" }, voice.CharacterTags);
    }

    [Fact]
    public async Task Series_endpoints_require_authentication_and_mutations_require_csrf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var anonymousClient = factory.CreateClient();
        using var anonymousResponse = await anonymousClient.GetAsync(
            "/api/series",
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var authenticatedClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var missingCsrfResponse = await authenticatedClient.PostAsJsonAsync(
            "/api/series",
            CreateSeriesBody("缺少 CSRF 的系列"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);
    }

    [Fact]
    public async Task Voice_catalog_requires_formal_admission_before_listing_private_bluemagpie_voices()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/series/voice-options", cancellationToken);
        var voices = await response.Content.ReadFromJsonAsync<SeriesVoiceOptionResponse[]>(
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(voices);
        Assert.Equal("female", voices.Single(voice => voice.Voice == "zh-TW-HsiaoChenNeural").Gender);
        Assert.Equal("male", voices.Single(voice => voice.Voice == "zh-TW-YunJheNeural").Gender);
        Assert.All(voices, voice =>
        {
            Assert.Null(voice.MinimumAge);
            Assert.Null(voice.MaximumAge);
            Assert.Empty(voice.CharacterTags!);
        });
        Assert.Contains(voices, voice =>
            voice.Provider == "voai"
            && voice.Voice == "v1:Neo:佑希:預設"
            && voice.DisplayName == "VoAI 佑希（Neo／預設）"
            && voice.Locale == "zh-TW");
        Assert.DoesNotContain(voices, voice =>
            string.Equals(voice.Provider, "3wa-voxcpm2", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(voices, voice =>
            string.Equals(voice.Provider, "bluemagpie", StringComparison.OrdinalIgnoreCase));

        using var enabledFactory = CreateFormalBlueMagpieFactory();
        using var enabledClient = await enabledFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var enabledVoices = await enabledClient.GetFromJsonAsync<SeriesVoiceOptionResponse[]>(
            "/api/series/voice-options",
            cancellationToken);
        Assert.NotNull(enabledVoices);
        var blueMagpieVoices = enabledVoices
            .Where(voice => voice.Provider == "bluemagpie")
            .OrderBy(voice => voice.Voice)
            .ToArray();
        Assert.Collection(
            blueMagpieVoices,
            voice =>
            {
                Assert.Equal("female_voice", voice.Voice);
                Assert.Equal("female", voice.Gender);
                Assert.Equal("BlueMagpie 內建女聲（私人自架）", voice.DisplayName);
                Assert.Equal("zh-TW", voice.Locale);
                Assert.True(voice.FormalNarrationAvailable);
                Assert.Equal("private-self-hosted", voice.UsageScope);
            },
            voice =>
            {
                Assert.Equal("hung_yi_lee", voice.Voice);
                Assert.Equal("male", voice.Gender);
                Assert.Equal("BlueMagpie 內建男聲（私人自架）", voice.DisplayName);
                Assert.Equal("zh-TW", voice.Locale);
                Assert.True(voice.FormalNarrationAvailable);
                Assert.Equal("private-self-hosted", voice.UsageScope);
            });
    }

    [Fact]
    public async Task Local_voice_preview_requires_authentication_and_csrf_and_fails_closed_when_disabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new SeriesVoicePreviewRequest("bluemagpie", "female_voice");

        using var anonymousClient = factory.CreateClient();
        using var anonymousResponse = await anonymousClient.PostAsJsonAsync(
            "/api/series/voice-preview",
            request,
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var missingCsrfResponse = await client.PostAsJsonAsync(
            "/api/series/voice-preview",
            request,
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);

        using var disabledResponse = await client.PostWithCsrfAsync(
            "/api/series/voice-preview",
            request,
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledResponse.StatusCode);
        using var problem = JsonDocument.Parse(
            await disabledResponse.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal(
            SeriesVoicePreviewUnavailableException.StableCode,
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Formal_bluemagpie_series_configuration_is_flag_gated_owner_scoped_atomic_and_neutral()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var disabledClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var disabledCreateResponse = await disabledClient.PostWithCsrfAsync(
            "/api/series",
            new
            {
                name = $"未開放本機配音 {Guid.NewGuid():N}",
                narratorProvider = "bluemagpie",
                narratorVoice = "female_voice",
                narratorRate = "+0%",
                narratorPitch = "+0Hz",
                narratorVolume = "+0%",
                defaultSpeakerPauseMs = 180
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, disabledCreateResponse.StatusCode);

        using var enabledFactory = CreateFormalBlueMagpieFactory();
        using var ownerClient = await enabledFactory.CreateAuthenticatedClientAsync(cancellationToken);
        using var otherOwnerClient = await enabledFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var series = await CreateSeriesAsync(ownerClient, $"原子聲線切換 {Guid.NewGuid():N}", cancellationToken);
        using var addCharacterResponse = await ownerClient.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = "褚冥漾",
                role = "Main",
                voiceProvider = "edge",
                voice = "zh-TW-YunJheNeural",
                rate = "-8%",
                pitch = "+2Hz",
                volume = "-3%",
                notes = (string?)null
            },
            cancellationToken);
        var withCharacter = await addCharacterResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, addCharacterResponse.StatusCode);
        var character = Assert.Single(Assert.IsType<StorySeriesDetailsResponse>(withCharacter).Characters);

        var validSwitch = new
        {
            narratorProvider = "bluemagpie",
            narratorVoice = "female_voice",
            characters = new[]
            {
                new { characterId = character.Id, voiceProvider = "bluemagpie", voice = "hung_yi_lee" }
            }
        };
        using var missingCsrfResponse = await ownerClient.PutAsJsonAsync(
            $"/api/series/{series.Id}/voices",
            validSwitch,
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);

        using var incompleteResponse = await ownerClient.PutWithCsrfAsync(
            $"/api/series/{series.Id}/voices",
            new
            {
                narratorProvider = "bluemagpie",
                narratorVoice = "female_voice",
                characters = Array.Empty<object>()
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, incompleteResponse.StatusCode);

        using var mixedProviderResponse = await ownerClient.PutWithCsrfAsync(
            $"/api/series/{series.Id}/voices",
            new
            {
                narratorProvider = "bluemagpie",
                narratorVoice = "female_voice",
                characters = new[]
                {
                    new { characterId = character.Id, voiceProvider = "edge", voice = "zh-TW-HsiaoChenNeural" }
                }
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, mixedProviderResponse.StatusCode);

        using var foreignOwnerResponse = await otherOwnerClient.PutWithCsrfAsync(
            $"/api/series/{series.Id}/voices",
            validSwitch,
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreignOwnerResponse.StatusCode);

        using var switchResponse = await ownerClient.PutWithCsrfAsync(
            $"/api/series/{series.Id}/voices",
            validSwitch,
            cancellationToken);
        var switched = await switchResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, switchResponse.StatusCode);
        Assert.NotNull(switched);
        Assert.Equal("bluemagpie", switched.NarratorProvider);
        Assert.Equal("female_voice", switched.NarratorVoice);
        Assert.Equal("+0%", switched.NarratorRate);
        Assert.Equal("+0Hz", switched.NarratorPitch);
        Assert.Equal("+0%", switched.NarratorVolume);
        var switchedCharacter = Assert.Single(switched.Characters);
        Assert.Equal("bluemagpie", switchedCharacter.VoiceProvider);
        Assert.Equal("hung_yi_lee", switchedCharacter.Voice);
        Assert.Equal("+0%", switchedCharacter.Rate);
        Assert.Equal("+0Hz", switchedCharacter.Pitch);
        Assert.Equal("+0%", switchedCharacter.Volume);
        Assert.Null(switched.ActiveCastRevisionId);

        using var mixedAddResponse = await ownerClient.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = "冰炎",
                role = "Supporting",
                voiceProvider = "edge",
                voice = "zh-TW-YunJheNeural",
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, mixedAddResponse.StatusCode);

        using var unsupportedParametersResponse = await ownerClient.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{character.Id}",
            new
            {
                canonicalName = character.CanonicalName,
                role = character.Role,
                voiceProvider = "bluemagpie",
                voice = "female_voice",
                rate = "+5%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedParametersResponse.StatusCode);
    }

    [Fact]
    public async Task Enabled_local_voice_preview_returns_private_audio_and_pinned_identity_headers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var enabledFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISeriesVoicePreviewService>();
                services.AddSingleton<ISeriesVoicePreviewService, FakeSeriesVoicePreviewService>();
            }));
        using var client = await enabledFactory.CreateAuthenticatedClientAsync(cancellationToken);

        using var response = await client.PostWithCsrfAsync(
            "/api/series/voice-preview",
            new SeriesVoicePreviewRequest("bluemagpie", "female_voice"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(
            "6f7cab914a1e27c56b504ec663c0144dc25cc0a3",
            Assert.Single(response.Headers.GetValues("X-BlueMagpie-Model-Revision")));
        Assert.Equal(
            "female_voice",
            Assert.Single(response.Headers.GetValues("X-BlueMagpie-Voice")));
        Assert.Equal(
            FakeSeriesVoicePreviewService.Audio,
            await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task Series_queries_are_owner_scoped_and_duplicate_names_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerAClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var ownerBClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var created = await CreateSeriesAsync(ownerAClient, "  公開測試系列  ", cancellationToken);

        using var listResponse = await ownerAClient.GetAsync("/api/series", cancellationToken);
        var ownerAList = await listResponse.Content.ReadFromJsonAsync<StorySeriesSummaryResponse[]>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.NotNull(ownerAList);
        Assert.Contains(ownerAList, series => series.Id == created.Id && series.Name == "公開測試系列");

        using var voiceResponse = await ownerAClient.GetAsync(
            "/api/series/voice-options",
            cancellationToken);
        var voices = await voiceResponse.Content.ReadFromJsonAsync<SeriesVoiceOptionResponse[]>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, voiceResponse.StatusCode);
        Assert.NotNull(voices);
        var expectedVoices = new[]
        {
            "zh-TW-HsiaoChenNeural",
            "zh-TW-YunJheNeural",
            "zh-TW-HsiaoYuNeural",
            "zh-CN-YunxiNeural",
            "zh-CN-YunjianNeural",
            "zh-CN-XiaoxiaoNeural",
            "zh-CN-XiaoyiNeural"
        };
        Assert.All(expectedVoices, expectedVoice =>
            Assert.Contains(voices, voice => voice.Voice == expectedVoice));

        using var ownerBListResponse = await ownerBClient.GetAsync("/api/series", cancellationToken);
        var ownerBList = await ownerBListResponse.Content.ReadFromJsonAsync<StorySeriesSummaryResponse[]>(
            cancellationToken);
        Assert.NotNull(ownerBList);
        Assert.Empty(ownerBList);

        using var ownerBGetResponse = await ownerBClient.GetAsync(
            $"/api/series/{created.Id}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, ownerBGetResponse.StatusCode);

        using var duplicateResponse = await ownerAClient.PostWithCsrfAsync(
            "/api/series",
            CreateSeriesBody("公開測試系列"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);
    }

    [Fact]
    public async Task Owner_can_manage_membership_cast_and_alias_without_exposing_book_text()
    {
        const string privateTextSentinel = "PRIVATE_TEXT_MUST_NOT_APPEAR_IN_SERIES_RESPONSE";
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await CreateBookAsync(client, privateTextSentinel, cancellationToken);
        var series = await CreateSeriesAsync(client, "角色配音系列", cancellationToken);

        using var addBookResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new
            {
                bookId = book.Id,
                volumeLabel = "第一冊",
                sortOrder = 1
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, addBookResponse.StatusCode);

        using var addCharacterResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = "艾莉絲",
                role = "Supporting",
                voiceProvider = "edge",
                voice = "zh-TW-HsiaoChenNeural",
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = "公開的角色配音提示"
            },
            cancellationToken);
        var withCharacter = await addCharacterResponse.Content
            .ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.True(
            addCharacterResponse.StatusCode == HttpStatusCode.OK,
            $"Unexpected response: {await addCharacterResponse.Content.ReadAsStringAsync(cancellationToken)}");
        Assert.NotNull(withCharacter);
        var character = Assert.Single(withCharacter.Characters);

        using var addAliasResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{character.Id}/aliases",
            new { alias = "隊長" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, addAliasResponse.StatusCode);

        using var duplicateAliasResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{character.Id}/aliases",
            new { alias = " 隊長 " },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateAliasResponse.StatusCode);

        using var disallowedVoiceResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = "未知聲線角色",
                role = "Minor",
                voiceProvider = "untrusted-provider",
                voice = "private-voice-id",
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, disallowedVoiceResponse.StatusCode);

        using var updateRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/series/{series.Id}/characters/{character.Id}")
        {
            Content = JsonContent.Create(new
            {
                canonicalName = "艾莉絲隊長",
                role = "Main",
                voiceProvider = "edge",
                voice = "zh-TW-HsiaoYuNeural",
                rate = "-5%",
                pitch = "+2Hz",
                volume = "-3%",
                notes = "使用者確認的固定聲線"
            })
        };
        using var updateResponse = await client.SendWithCsrfAsync(
            updateRequest,
            cancellationToken);
        var updated = await updateResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.NotNull(updated);
        var updatedCharacter = Assert.Single(updated.Characters);
        Assert.Equal("艾莉絲隊長", updatedCharacter.CanonicalName);
        Assert.Equal("Main", updatedCharacter.Role);
        Assert.Equal("zh-TW-HsiaoYuNeural", updatedCharacter.Voice);
        Assert.Equal("隊長", Assert.Single(updatedCharacter.Aliases).Value);

        using var getResponse = await client.GetAsync($"/api/series/{series.Id}", cancellationToken);
        var responseText = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.DoesNotContain(privateTextSentinel, responseText, StringComparison.Ordinal);
        Assert.Contains("角色配音系列", responseText, StringComparison.Ordinal);
        Assert.Contains("第一冊", responseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Point_of_view_character_can_be_set_cleared_and_is_owner_scoped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerAClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var ownerBClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var series = await CreateSeriesAsync(ownerAClient, "視角角色系列", cancellationToken);

        using var addCharacterResponse = await ownerAClient.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = "褚冥漾",
                role = "Main",
                voiceProvider = "edge",
                voice = "zh-TW-YunJheNeural",
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null
            },
            cancellationToken);
        var withCharacter = await addCharacterResponse.Content
            .ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.NotNull(withCharacter);
        var character = Assert.Single(withCharacter.Characters);

        using var setResponse = await ownerAClient.PutWithCsrfAsync(
            $"/api/series/{series.Id}/point-of-view-character",
            new { characterId = character.Id },
            cancellationToken);
        var withPov = await setResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);
        Assert.NotNull(withPov);
        Assert.Equal(character.Id, withPov.PointOfViewCharacterId);

        using var clearResponse = await ownerAClient.PutWithCsrfAsync(
            $"/api/series/{series.Id}/point-of-view-character",
            new { characterId = (Guid?)null },
            cancellationToken);
        var cleared = await clearResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
        Assert.NotNull(cleared);
        Assert.Null(cleared.PointOfViewCharacterId);

        using var foreignCharacterResponse = await ownerAClient.PutWithCsrfAsync(
            $"/api/series/{series.Id}/point-of-view-character",
            new { characterId = Guid.NewGuid() },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, foreignCharacterResponse.StatusCode);

        using var ownerBResponse = await ownerBClient.PutWithCsrfAsync(
            $"/api/series/{series.Id}/point-of-view-character",
            new { characterId = character.Id },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, ownerBResponse.StatusCode);
    }

    [Fact]
    public async Task Non_owner_cannot_attach_books_or_mutate_another_users_series()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerAClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var ownerBClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var ownerASeries = await CreateSeriesAsync(ownerAClient, "擁有者 A 系列", cancellationToken);
        var ownerBBook = await CreateBookAsync(ownerBClient, "Synthetic public text", cancellationToken);

        using var ownerAAddsForeignBookResponse = await ownerAClient.PostWithCsrfAsync(
            $"/api/series/{ownerASeries.Id}/books",
            new { bookId = ownerBBook.Id, volumeLabel = "外部冊次", sortOrder = 1 },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, ownerAAddsForeignBookResponse.StatusCode);

        using var ownerBMutatesForeignSeriesResponse = await ownerBClient.PostWithCsrfAsync(
            $"/api/series/{ownerASeries.Id}/characters",
            new
            {
                canonicalName = "越權角色",
                role = "Main",
                voiceProvider = "edge",
                voice = "zh-TW-YunJheNeural",
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, ownerBMutatesForeignSeriesResponse.StatusCode);
    }

    private static object CreateSeriesBody(string name) => new
    {
        name,
        narratorProvider = "edge",
        narratorVoice = "zh-TW-YunJheNeural",
        narratorRate = "-5%",
        narratorPitch = "+0Hz",
        narratorVolume = "+0%",
        defaultSpeakerPauseMs = 350
    };

    private WebApplicationFactory<Program> CreateFormalBlueMagpieFactory() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("BlueMagpie:Enabled", "true");
            builder.UseSetting("BlueMagpie:FormalNarrationEnabled", "true");
            builder.UseSetting("BlueMagpie:InternalToken", new string('t', 32));
        });

    private sealed class FakeSeriesVoicePreviewService : ISeriesVoicePreviewService
    {
        internal static readonly byte[] Audio = [82, 73, 70, 70, 87, 65, 86, 69];

        public Task<SeriesVoicePreviewAudio> GenerateAsync(
            SeriesVoicePreviewRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SeriesVoicePreviewAudio(
                Audio,
                "audio/wav",
                "6f7cab914a1e27c56b504ec663c0144dc25cc0a3",
                request.Voice));
    }

    private static async Task<StorySeriesDetailsResponse> CreateSeriesAsync(
        HttpClient client,
        string name,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostWithCsrfAsync(
            "/api/series",
            CreateSeriesBody(name),
            cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<StorySeriesDetailsResponse>(created);
    }

    private static async Task<BookDetailsResponse> CreateBookAsync(
        HttpClient client,
        string originalText,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostWithCsrfAsync(
            "/api/books",
            new CreateBookRequest(
                $"Synthetic book {Guid.NewGuid():N}",
                "Synthetic author",
                "zh-TW",
                "synthetic.txt",
                [new CreateChapterRequest(1, "序章", originalText)]),
            cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<BookDetailsResponse>(created);
    }
}

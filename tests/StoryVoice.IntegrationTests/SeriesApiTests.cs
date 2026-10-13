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
    public async Task Legacy_operator_catalog_cannot_introduce_unmanaged_voices()
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
        Assert.Empty(response!);
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
    public async Task Enabling_legacy_provider_does_not_add_unmanaged_voices()
    {
        var ct = TestContext.Current.CancellationToken;
        using var enabledFactory = CreateFormalBlueMagpieFactory();
        using var client = await enabledFactory.CreateAuthenticatedClientAsync(ct);
        Assert.Empty((await client.GetFromJsonAsync<SeriesVoiceOptionResponse[]>("/api/series/voice-options", ct))!);
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

    [Theory]
    [InlineData("edge", "zh-TW-YunJheNeural")]
    [InlineData("bluemagpie", "female_voice")]
    [InlineData("voai", "v1:Neo:佑希:預設")]
    public async Task Legacy_voice_switch_is_rejected_atomically(string provider, string voice)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(ct);
        var series = await CreateSeriesAsync(client, "不可使用內建聲線", ct);
        using var rejected = await client.PutWithCsrfAsync($"/api/series/{series.Id}/voices",
            new { narratorProvider = provider, narratorVoice = voice, characters = Array.Empty<object>() }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var after = await client.GetFromJsonAsync<StorySeriesDetailsResponse>($"/api/series/{series.Id}", ct);
        Assert.Equal(series.NarratorVoice, after!.NarratorVoice);
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
        Assert.Equal(created.NarratorVoice, Assert.Single(voices).Voice);

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
            CreateSeriesBody("公開測試系列", created.NarratorVoice),
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
                voiceProvider = "local-clone",
                voice = await client.ManagedVoiceAsync(cancellationToken, "actor"),
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
                voiceProvider = "local-clone",
                voice = await client.ManagedVoiceAsync(cancellationToken, "actor-new"),
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
        Assert.Equal(await client.ManagedVoiceAsync(cancellationToken, "actor-new"), updatedCharacter.Voice);
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
                voiceProvider = "local-clone",
                voice = await ownerAClient.ManagedVoiceAsync(cancellationToken, "default"),
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
                voiceProvider = "local-clone",
                voice = await ownerAClient.ManagedVoiceAsync(cancellationToken, "default"),
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, ownerBMutatesForeignSeriesResponse.StatusCode);
    }

    private static object CreateSeriesBody(string name, string voice = "localclone:11111111111111111111111111111111") => new
    {
        name,
        narratorProvider = "local-clone",
        narratorVoice = voice,
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
            CreateSeriesBody(name, await client.ManagedVoiceAsync(cancellationToken)),
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

using System.Net;
using System.Net.Http.Json;
using StoryVoice.Application.Characters;
using StoryVoice.Application.Series;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.IntegrationTests;

public sealed class CharacterProfileApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Character_profile_endpoints_require_authentication_and_mutations_require_csrf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var anonymousClient = factory.CreateClient();
        using var anonymousResponse = await anonymousClient.GetAsync("/api/character-profiles", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var missingCsrfResponse = await client.PostAsJsonAsync(
            "/api/character-profiles",
            CreateCharacterBody("缺少 CSRF 的角色"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);
    }

    [Fact]
    public async Task A_character_profile_can_be_created_updated_and_listed_with_all_bio_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        using var createResponse = await client.PostWithCsrfAsync(
            "/api/character-profiles",
            new
            {
                canonicalName = "小羽",
                age = "16",
                gender = "女",
                birthday = "2009-11-23",
                personality = "溫柔、細心、善解人意，偶爾有點害羞",
                catchphrase = "「嗯～我想想…」、「沒問題的，我會努力的！」",
                background = "住在海邊小鎮的高中生，喜歡繪畫與音樂。",
                speakingStyle = "語氣輕柔、語速偏慢，常用敬語"
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken);
        Assert.NotNull(created);
        Assert.Equal("小羽", created.CanonicalName);
        Assert.Equal("16", created.Age);
        Assert.False(created.HasAvatar);

        using var listResponse = await client.GetAsync("/api/character-profiles", cancellationToken);
        var list = await listResponse.Content.ReadFromJsonAsync<CharacterProfileResponse[]>(cancellationToken);
        Assert.NotNull(list);
        Assert.Contains(list, profile => profile.Id == created.Id);

        using var updateResponse = await client.PutWithCsrfAsync(
            $"/api/character-profiles/{created.Id}",
            new
            {
                canonicalName = "小羽",
                age = "17",
                gender = "女",
                birthday = "2009-11-23",
                personality = "更加成熟穩重了",
                catchphrase = created.Catchphrase,
                background = created.Background,
                speakingStyle = created.SpeakingStyle
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("17", updated.Age);
        Assert.Equal("更加成熟穩重了", updated.Personality);
    }

    [Fact]
    public async Task Character_profiles_are_owner_scoped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerAClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var ownerBClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        using var createResponse = await ownerAClient.PostWithCsrfAsync(
            "/api/character-profiles",
            CreateCharacterBody("只有 A 看得到的角色"),
            cancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken);
        Assert.NotNull(created);

        using var ownerBGetResponse = await ownerBClient.GetAsync(
            $"/api/character-profiles/{created.Id}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, ownerBGetResponse.StatusCode);

        using var ownerBListResponse = await ownerBClient.GetAsync("/api/character-profiles", cancellationToken);
        var ownerBList = await ownerBListResponse.Content.ReadFromJsonAsync<CharacterProfileResponse[]>(cancellationToken);
        Assert.NotNull(ownerBList);
        Assert.DoesNotContain(ownerBList, profile => profile.Id == created.Id);
    }

    [Fact]
    public async Task A_character_profile_can_be_picked_when_adding_a_series_character()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        using var characterProfileResponse = await client.PostWithCsrfAsync(
            "/api/character-profiles",
            CreateCharacterBody("跨系列共用角色"),
            cancellationToken);
        var characterProfile = await characterProfileResponse.Content
            .ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken);
        Assert.NotNull(characterProfile);

        using var seriesResponse = await client.PostWithCsrfAsync(
            "/api/series",
            new
            {
                name = $"角色庫測試系列-{Guid.NewGuid():N}",
                narratorProvider = "local-clone",
                narratorVoice = await client.ManagedVoiceAsync(cancellationToken),
                narratorRate = "-5%",
                narratorPitch = "+0Hz",
                narratorVolume = "+0%",
                defaultSpeakerPauseMs = 350
            },
            cancellationToken);
        var series = await seriesResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.NotNull(series);

        using var addCharacterResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = characterProfile.CanonicalName,
                role = "Supporting",
                voiceProvider = "local-clone",
                voice = await client.ManagedVoiceAsync(cancellationToken),
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null,
                characterProfileId = characterProfile.Id
            },
            cancellationToken);
        Assert.True(
            addCharacterResponse.StatusCode == HttpStatusCode.OK,
            $"Unexpected response: {await addCharacterResponse.Content.ReadAsStringAsync(cancellationToken)}");
        var withCharacter = await addCharacterResponse.Content
            .ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.NotNull(withCharacter);
        var seriesCharacter = Assert.Single(withCharacter.Characters);
        Assert.Equal(characterProfile.Id, seriesCharacter.CharacterProfileId);

        using var unknownProfileResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = "另一個角色",
                role = "Supporting",
                voiceProvider = "local-clone",
                voice = await client.ManagedVoiceAsync(cancellationToken),
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null,
                characterProfileId = Guid.NewGuid()
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unknownProfileResponse.StatusCode);
    }

    [Fact]
    public async Task Dedicated_series_link_is_owner_scoped_preserves_cast_and_enforces_active_unique_profiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var otherOwner = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var firstProfile = await CreateProfileAsync(owner, "第一位共用角色", cancellationToken);
        var secondProfile = await CreateProfileAsync(owner, "第二位共用角色", cancellationToken);
        var foreignProfile = await CreateProfileAsync(otherOwner, "外部角色", cancellationToken);
        using var seriesResponse = await owner.PostWithCsrfAsync(
            "/api/series",
            new
            {
                name = $"專用角色庫連結-{Guid.NewGuid():N}",
                narratorProvider = "local-clone",
                narratorVoice = await owner.ManagedVoiceAsync(cancellationToken),
                narratorRate = "+0%",
                narratorPitch = "+0Hz",
                narratorVolume = "+0%",
                defaultSpeakerPauseMs = 350
            },
            cancellationToken);
        var series = await seriesResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.NotNull(series);

        StorySeriesDetailsResponse current = series;
        foreach (var character in new[]
                 {
                     new { Name = "Alice", Role = "Main", Notes = "必須保留的主角提示" },
                     new { Name = "Bob", Role = "Supporting", Notes = "必須保留的配角提示" }
                 })
        {
            using var addResponse = await owner.PostWithCsrfAsync(
                $"/api/series/{series.Id}/characters",
                new
                {
                    canonicalName = character.Name,
                    role = character.Role,
                    voiceProvider = "local-clone",
                    voice = await owner.ManagedVoiceAsync(cancellationToken),
                    rate = "-5%",
                    pitch = "+2Hz",
                    volume = "-3%",
                    notes = character.Notes
                },
                cancellationToken);
            current = Assert.IsType<StorySeriesDetailsResponse>(
                await addResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken));
            Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        }

        var firstCharacter = current.Characters.Single(character => character.CanonicalName == "Alice");
        var secondCharacter = current.Characters.Single(character => character.CanonicalName == "Bob");

        using var otherOwnerResponse = await otherOwner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{firstCharacter.Id}/character-profile",
            new { characterProfileId = foreignProfile.Id },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherOwnerResponse.StatusCode);

        using var missingCharacterResponse = await owner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{Guid.NewGuid()}/character-profile",
            new { characterProfileId = firstProfile.Id },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missingCharacterResponse.StatusCode);

        using var linkFirstResponse = await owner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{firstCharacter.Id}/character-profile",
            new { characterProfileId = firstProfile.Id },
            cancellationToken);
        var linked = await linkFirstResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, linkFirstResponse.StatusCode);
        var linkedFirst = Assert.IsType<StorySeriesDetailsResponse>(linked).Characters
            .Single(character => character.Id == firstCharacter.Id);
        Assert.Equal(firstProfile.Id, linkedFirst.CharacterProfileId);
        Assert.Equal(
            (
                firstCharacter.CanonicalName,
                firstCharacter.Role,
                firstCharacter.VoiceProvider,
                firstCharacter.Voice,
                firstCharacter.Rate,
                firstCharacter.Pitch,
                firstCharacter.Volume,
                firstCharacter.Notes),
            (
                linkedFirst.CanonicalName,
                linkedFirst.Role,
                linkedFirst.VoiceProvider,
                linkedFirst.Voice,
                linkedFirst.Rate,
                linkedFirst.Pitch,
                linkedFirst.Volume,
                linkedFirst.Notes));

        using var duplicateResponse = await owner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{secondCharacter.Id}/character-profile",
            new { characterProfileId = firstProfile.Id },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);

        using var foreignProfileResponse = await owner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{secondCharacter.Id}/character-profile",
            new { characterProfileId = foreignProfile.Id },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, foreignProfileResponse.StatusCode);

        using var linkSecondResponse = await owner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{secondCharacter.Id}/character-profile",
            new { characterProfileId = secondProfile.Id },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, linkSecondResponse.StatusCode);

        using var deactivateResponse = await owner.PostWithCsrfAsync(
            $"/api/character-profiles/{secondProfile.Id}/deactivate",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);

        using var inactiveNoOpResponse = await owner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{secondCharacter.Id}/character-profile",
            new { characterProfileId = secondProfile.Id },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, inactiveNoOpResponse.StatusCode);

        using var unlinkResponse = await owner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{secondCharacter.Id}/character-profile",
            new { characterProfileId = (Guid?)null },
            cancellationToken);
        var unlinked = await unlinkResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, unlinkResponse.StatusCode);
        Assert.Null(Assert.IsType<StorySeriesDetailsResponse>(unlinked).Characters
            .Single(character => character.Id == secondCharacter.Id).CharacterProfileId);

        using var inactiveNewLinkResponse = await owner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{secondCharacter.Id}/character-profile",
            new { characterProfileId = secondProfile.Id },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, inactiveNewLinkResponse.StatusCode);

        using var inactiveAddResponse = await owner.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = "Carol",
                role = "Minor",
                voiceProvider = "local-clone",
                voice = await owner.ManagedVoiceAsync(cancellationToken),
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null,
                characterProfileId = secondProfile.Id
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, inactiveAddResponse.StatusCode);

        using var inactiveFullUpdateResponse = await owner.PutWithCsrfAsync(
            $"/api/series/{series.Id}/characters/{secondCharacter.Id}",
            new
            {
                canonicalName = secondCharacter.CanonicalName,
                role = secondCharacter.Role,
                voiceProvider = secondCharacter.VoiceProvider,
                voice = secondCharacter.Voice,
                rate = secondCharacter.Rate,
                pitch = secondCharacter.Pitch,
                volume = secondCharacter.Volume,
                notes = secondCharacter.Notes,
                characterProfileId = secondProfile.Id
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, inactiveFullUpdateResponse.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_character_profile_still_linked_to_a_series_character_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        using var characterProfileResponse = await client.PostWithCsrfAsync(
            "/api/character-profiles",
            CreateCharacterBody("被系列使用中的角色"),
            cancellationToken);
        var characterProfile = await characterProfileResponse.Content
            .ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken);
        Assert.NotNull(characterProfile);

        using var seriesResponse = await client.PostWithCsrfAsync(
            "/api/series",
            new
            {
                name = $"刪除保護測試系列-{Guid.NewGuid():N}",
                narratorProvider = "local-clone",
                narratorVoice = await client.ManagedVoiceAsync(cancellationToken),
                narratorRate = "-5%",
                narratorPitch = "+0Hz",
                narratorVolume = "+0%",
                defaultSpeakerPauseMs = 350
            },
            cancellationToken);
        var series = await seriesResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.NotNull(series);

        using var addCharacterResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = characterProfile.CanonicalName,
                role = "Supporting",
                voiceProvider = "local-clone",
                voice = await client.ManagedVoiceAsync(cancellationToken),
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null,
                characterProfileId = characterProfile.Id
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, addCharacterResponse.StatusCode);

        using var deleteResponse = await client.DeleteWithCsrfAsync(
            $"/api/character-profiles/{characterProfile.Id}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task A_character_profile_starts_active_and_can_be_deactivated_and_reactivated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        using var createResponse = await client.PostWithCsrfAsync(
            "/api/character-profiles",
            CreateCharacterBody("啟用狀態測試角色"),
            cancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken);
        Assert.NotNull(created);
        Assert.True(created.IsActive);

        using var deactivateResponse = await client.PostWithCsrfAsync(
            $"/api/character-profiles/{created.Id}/deactivate",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);
        var deactivated = await deactivateResponse.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken);
        Assert.NotNull(deactivated);
        Assert.False(deactivated.IsActive);

        using var activateResponse = await client.PostWithCsrfAsync(
            $"/api/character-profiles/{created.Id}/activate",
            new { },
            cancellationToken);
        var activated = await activateResponse.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken);
        Assert.NotNull(activated);
        Assert.True(activated.IsActive);
    }

    private static object CreateCharacterBody(string canonicalName) => new
    {
        canonicalName,
        age = (string?)null,
        gender = (string?)null,
        birthday = (string?)null,
        personality = (string?)null,
        catchphrase = (string?)null,
        background = (string?)null,
        speakingStyle = (string?)null
    };

    private static async Task<CharacterProfileResponse> CreateProfileAsync(
        HttpClient client,
        string canonicalName,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostWithCsrfAsync(
            "/api/character-profiles",
            CreateCharacterBody(canonicalName),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CharacterProfileResponse>(
            await response.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken));
    }
}

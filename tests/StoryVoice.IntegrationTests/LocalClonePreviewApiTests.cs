using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryVoice.Application.Characters;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Characters;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class LocalClonePreviewApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Endpoints_require_auth_csrf_and_owner_scope_and_disabled_preview_fails_closed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seriesId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        using var anonymous = factory.CreateClient();
        using var anonymousGet = await anonymous.GetAsync(
            PreviewPath(seriesId, characterId),
            cancellationToken);
        using var anonymousPost = await anonymous.PostAsJsonAsync(
            PreviewPath(seriesId, characterId),
            new LocalClonePreviewRequest("匿名試音"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousGet.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPost.StatusCode);

        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var created = await CreateSeriesCharacterAsync(owner, profileId: null, cancellationToken);
        using var availability = await owner.GetAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            cancellationToken);
        var availabilityBody = await availability.Content
            .ReadFromJsonAsync<LocalClonePreviewAvailabilityResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
        Assert.Contains("no-store", availability.Headers.CacheControl?.ToString());
        Assert.NotNull(availabilityBody);
        Assert.False(availabilityBody.Available);
        Assert.Null(availabilityBody.Label);

        using var missingCsrf = await owner.PostAsJsonAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            new LocalClonePreviewRequest("缺少 CSRF"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        using var otherOwner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var foreignGet = await otherOwner.GetAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            cancellationToken);
        using var foreignPost = await otherOwner.PostWithCsrfAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            new LocalClonePreviewRequest("越權試音"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreignGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignPost.StatusCode);

        using var disabled = await owner.PostWithCsrfAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            new LocalClonePreviewRequest("停用試音"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        Assert.Equal(
            "local_clone_preview_disabled",
            await ReadProblemCodeAsync(disabled, cancellationToken));

        using var createProfile = await owner.PostWithCsrfAsync(
            "/api/character-profiles",
            new
            {
                canonicalName = "停用功能的私人試音角色",
                age = (string?)null,
                gender = (string?)null,
                birthday = (string?)null,
                personality = (string?)null,
                catchphrase = (string?)null,
                background = (string?)null,
                speakingStyle = (string?)null,
            },
            cancellationToken);
        var profile = Assert.IsType<CharacterProfileResponse>(
            await createProfile.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken));
        using var disabledProfileAvailability = await owner.GetAsync(
            ProfilePreviewPath(profile.Id),
            cancellationToken);
        var disabledProfileBody = await disabledProfileAvailability.Content
            .ReadFromJsonAsync<LocalClonePreviewAvailabilityResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, disabledProfileAvailability.StatusCode);
        Assert.NotNull(disabledProfileBody);
        Assert.False(disabledProfileBody.Available);
        using var disabledProfilePreview = await owner.PostWithCsrfAsync(
            ProfilePreviewPath(profile.Id),
            new LocalClonePreviewRequest("停用功能不可試音"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledProfilePreview.StatusCode);
        Assert.Equal(
            "local_clone_preview_disabled",
            await ReadProblemCodeAsync(disabledProfilePreview, cancellationToken));
    }

    [Fact]
    public async Task Exact_profile_allowlist_returns_private_wav_without_changing_formal_db_pointers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var transcript = "大家好，這是周子謙授權的參考逐字稿。";
        var reference = CreatePcmWave(48_000, durationSeconds: 10);
        var output = CreatePcmWave(24_000, durationSeconds: 1);
        var fake = new FakeLocalCloneGatewayClient(output);
        var assetRoot = await WriteAssetsAsync(profileId, reference, transcript, cancellationToken);
        using var enabledFactory = CreateEnabledFactory(
            profileId,
            assetRoot,
            Sha256(reference),
            CharacterVoiceTranscriptCanonicalizer.ComputeSha256Hex(transcript),
            fake);
        using var owner = await CreateOwnerWithProfileAsync(
            enabledFactory,
            profileId,
            cancellationToken);
        var created = await CreateSeriesCharacterAsync(owner, profileId, cancellationToken);
        var before = await ReadFormalDbStateAsync(enabledFactory, created, cancellationToken);

        using var availability = await owner.GetAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            cancellationToken);
        var availabilityBody = await availability.Content
            .ReadFromJsonAsync<LocalClonePreviewAvailabilityResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
        Assert.NotNull(availabilityBody);
        Assert.True(availabilityBody.Available);
        Assert.Equal("褚冥漾／周子謙（私人試音）", availabilityBody.Label);

        using var preview = await owner.PostWithCsrfAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            new LocalClonePreviewRequest("  褚冥漾的私人試音。  "),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("audio/wav", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal(output.LongLength, preview.Content.Headers.ContentLength);
        Assert.Contains("no-store", preview.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", preview.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(output, await preview.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(1, fake.Calls);
        Assert.Equal("褚冥漾的私人試音。", fake.LastRequest?.Text);
        Assert.Equal(
            CharacterVoiceTranscriptCanonicalizer.Normalize(transcript),
            fake.LastRequest?.ReferenceTranscript);
        Assert.Equal(reference, fake.LastRequest?.ReferenceAudio);
        Assert.False(fake.LastCancellationToken.CanBeCanceled);

        var after = await ReadFormalDbStateAsync(enabledFactory, created, cancellationToken);
        Assert.Equal(before, after);

        await using (var scope = enabledFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var profile = await db.CharacterProfiles.SingleAsync(
                candidate => candidate.Id == profileId,
                cancellationToken);
            profile.Deactivate(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }

        using var inactiveAvailability = await owner.GetAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            cancellationToken);
        var inactiveBody = await inactiveAvailability.Content
            .ReadFromJsonAsync<LocalClonePreviewAvailabilityResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, inactiveAvailability.StatusCode);
        Assert.NotNull(inactiveBody);
        Assert.False(inactiveBody.Available);

        using var inactivePreview = await owner.PostWithCsrfAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            new LocalClonePreviewRequest("停用後不可試音"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, inactivePreview.StatusCode);
        Assert.Equal(
            "local_clone_preview_not_configured",
            await ReadProblemCodeAsync(inactivePreview, cancellationToken));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Character_profile_preview_is_owner_scoped_and_works_without_a_series_link()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var transcript = "林若晴授權的參考逐字稿。";
        var reference = CreatePcmWave(48_000, durationSeconds: 10);
        var output = CreatePcmWave(24_000, durationSeconds: 1);
        var fake = new FakeLocalCloneGatewayClient(output);
        var assetRoot = await WriteAssetsAsync(profileId, reference, transcript, cancellationToken);
        using var enabledFactory = CreateEnabledFactory(
            profileId,
            assetRoot,
            Sha256(reference),
            CharacterVoiceTranscriptCanonicalizer.ComputeSha256Hex(transcript),
            fake,
            label: "林若晴（私人試音）");
        using var owner = await CreateOwnerWithProfileAsync(
            enabledFactory,
            profileId,
            cancellationToken);

        await using (var scope = enabledFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            Assert.False(await db.SeriesCharacters.AnyAsync(
                character => character.CharacterProfileId == profileId,
                cancellationToken));
        }

        using var anonymous = enabledFactory.CreateClient();
        using var anonymousGet = await anonymous.GetAsync(
            ProfilePreviewPath(profileId),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousGet.StatusCode);

        using var availability = await owner.GetAsync(
            ProfilePreviewPath(profileId),
            cancellationToken);
        var availabilityBody = await availability.Content
            .ReadFromJsonAsync<LocalClonePreviewAvailabilityResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
        Assert.Contains("no-store", availability.Headers.CacheControl?.ToString());
        Assert.NotNull(availabilityBody);
        Assert.True(availabilityBody.Available);
        Assert.Equal("林若晴（私人試音）", availabilityBody.Label);

        using var missingCsrf = await owner.PostAsJsonAsync(
            ProfilePreviewPath(profileId),
            new LocalClonePreviewRequest("缺少 CSRF"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        using var otherOwner = await enabledFactory.CreateAuthenticatedClientAsync(cancellationToken);
        using var foreignGet = await otherOwner.GetAsync(
            ProfilePreviewPath(profileId),
            cancellationToken);
        using var foreignPost = await otherOwner.PostWithCsrfAsync(
            ProfilePreviewPath(profileId),
            new LocalClonePreviewRequest("越權試音"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreignGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignPost.StatusCode);

        const string previewText = "這是角色私人聲線試音，僅用於確認聲音，不屬於有聲書正文。";
        using var preview = await owner.PostWithCsrfAsync(
            ProfilePreviewPath(profileId),
            new LocalClonePreviewRequest(previewText),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("audio/wav", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal(output.LongLength, preview.Content.Headers.ContentLength);
        Assert.Contains("no-store", preview.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", preview.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(output, await preview.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(1, fake.Calls);
        Assert.Equal(previewText, fake.LastRequest?.Text);

        using var unlistedCreate = await owner.PostWithCsrfAsync(
            "/api/character-profiles",
            new
            {
                canonicalName = "未列入試音角色",
                age = (string?)null,
                gender = (string?)null,
                birthday = (string?)null,
                personality = (string?)null,
                catchphrase = (string?)null,
                background = (string?)null,
                speakingStyle = (string?)null,
            },
            cancellationToken);
        var unlisted = Assert.IsType<CharacterProfileResponse>(
            await unlistedCreate.Content.ReadFromJsonAsync<CharacterProfileResponse>(cancellationToken));
        using var unlistedAvailability = await owner.GetAsync(
            ProfilePreviewPath(unlisted.Id),
            cancellationToken);
        var unlistedBody = await unlistedAvailability.Content
            .ReadFromJsonAsync<LocalClonePreviewAvailabilityResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, unlistedAvailability.StatusCode);
        Assert.NotNull(unlistedBody);
        Assert.False(unlistedBody.Available);
        Assert.Null(unlistedBody.Label);
        using var unlistedPreview = await owner.PostWithCsrfAsync(
            ProfilePreviewPath(unlisted.Id),
            new LocalClonePreviewRequest("未列入角色"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unlistedPreview.StatusCode);
        Assert.Equal(
            "local_clone_preview_not_configured",
            await ReadProblemCodeAsync(unlistedPreview, cancellationToken));
        Assert.Equal(1, fake.Calls);

        using var deactivate = await owner.PostWithCsrfAsync(
            $"/api/character-profiles/{profileId}/deactivate",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        using var inactiveAvailability = await owner.GetAsync(
            ProfilePreviewPath(profileId),
            cancellationToken);
        var inactiveBody = await inactiveAvailability.Content
            .ReadFromJsonAsync<LocalClonePreviewAvailabilityResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, inactiveAvailability.StatusCode);
        Assert.NotNull(inactiveBody);
        Assert.False(inactiveBody.Available);
        using var inactivePreview = await owner.PostWithCsrfAsync(
            ProfilePreviewPath(profileId),
            new LocalClonePreviewRequest("停用角色"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, inactivePreview.StatusCode);
        Assert.Equal(
            "local_clone_preview_not_configured",
            await ReadProblemCodeAsync(inactivePreview, cancellationToken));
        Assert.Equal(1, fake.Calls);

        await using (var scope = enabledFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            Assert.False(await db.SeriesCharacters.AnyAsync(
                character => character.CharacterProfileId == profileId,
                cancellationToken));
            Assert.False(await db.CharacterVoiceProfiles.AnyAsync(
                profile => profile.CharacterProfileId == profileId,
                cancellationToken));
        }
    }

    [Fact]
    public async Task Enabled_preview_rejects_an_unlisted_linked_profile_before_gateway_use()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var allowedProfileId = Guid.NewGuid();
        var linkedProfileId = Guid.NewGuid();
        var transcript = "合法逐字稿";
        var reference = CreatePcmWave(48_000, durationSeconds: 10);
        var fake = new FakeLocalCloneGatewayClient(CreatePcmWave(24_000, 1));
        var assetRoot = await WriteAssetsAsync(
            allowedProfileId,
            reference,
            transcript,
            cancellationToken);
        using var enabledFactory = CreateEnabledFactory(
            allowedProfileId,
            assetRoot,
            Sha256(reference),
            CharacterVoiceTranscriptCanonicalizer.ComputeSha256Hex(transcript),
            fake);
        using var owner = await CreateOwnerWithProfileAsync(
            enabledFactory,
            linkedProfileId,
            cancellationToken);
        var created = await CreateSeriesCharacterAsync(owner, linkedProfileId, cancellationToken);

        using var availability = await owner.GetAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            cancellationToken);
        var availabilityBody = await availability.Content
            .ReadFromJsonAsync<LocalClonePreviewAvailabilityResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
        Assert.NotNull(availabilityBody);
        Assert.False(availabilityBody.Available);

        using var preview = await owner.PostWithCsrfAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            new LocalClonePreviewRequest("未列入角色"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, preview.StatusCode);
        Assert.Equal(
            "local_clone_preview_not_configured",
            await ReadProblemCodeAsync(preview, cancellationToken));
        Assert.Equal(0, fake.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Invalid_hash_or_private_asset_path_fails_before_gateway_use(
        bool missingPath,
        bool transcriptHashMismatch)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var transcript = "雜湊與路徑測試逐字稿";
        var reference = CreatePcmWave(48_000, durationSeconds: 10);
        var fake = new FakeLocalCloneGatewayClient(CreatePcmWave(24_000, 1));
        var assetRoot = await WriteAssetsAsync(profileId, reference, transcript, cancellationToken);
        using var enabledFactory = CreateEnabledFactory(
            profileId,
            assetRoot,
            missingPath || transcriptHashMismatch ? Sha256(reference) : new string('0', 64),
            transcriptHashMismatch
                ? new string('0', 64)
                : CharacterVoiceTranscriptCanonicalizer.ComputeSha256Hex(transcript),
            fake,
            referencePath: missingPath ? "missing/reference.wav" : "reference.wav");
        using var owner = await CreateOwnerWithProfileAsync(
            enabledFactory,
            profileId,
            cancellationToken);
        var created = await CreateSeriesCharacterAsync(owner, profileId, cancellationToken);

        using var preview = await owner.PostWithCsrfAsync(
            PreviewPath(created.SeriesId, created.CharacterId),
            new LocalClonePreviewRequest("資產錯誤"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, preview.StatusCode);
        Assert.Equal(
            "local_clone_preview_asset_invalid",
            await ReadProblemCodeAsync(preview, cancellationToken));
        Assert.Equal(0, fake.Calls);
    }

    private WebApplicationFactory<Program> CreateEnabledFactory(
        Guid profileId,
        string assetRoot,
        string referenceSha256,
        string transcriptSha256,
        FakeLocalCloneGatewayClient fake,
        string label = "褚冥漾／周子謙（私人試音）",
        string referencePath = "reference.wav") =>
        factory.WithWebHostBuilder(builder =>
        {
            var prefix = $"{LocalClonePreviewOptions.SectionName}:AllowedProfiles:{profileId:D}";
            builder.UseSetting($"{LocalClonePreviewOptions.SectionName}:Enabled", "true");
            builder.UseSetting(
                $"{LocalClonePreviewOptions.SectionName}:InternalToken",
                new string('t', 32));
            builder.UseSetting($"{LocalClonePreviewOptions.SectionName}:AssetRootPath", assetRoot);
            builder.UseSetting($"{prefix}:Label", label);
            builder.UseSetting($"{prefix}:ReferenceAudioRelativePath", referencePath);
            builder.UseSetting($"{prefix}:TranscriptRelativePath", "transcript.txt");
            builder.UseSetting($"{prefix}:ExpectedReferenceAudioSha256", referenceSha256);
            builder.UseSetting($"{prefix}:ExpectedTranscriptSha256", transcriptSha256);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILocalCloneGatewayClient>();
                services.AddSingleton<ILocalCloneGatewayClient>(fake);
            });
        });

    private static async Task<HttpClient> CreateOwnerWithProfileAsync(
        WebApplicationFactory<Program> enabledFactory,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var email = $"local-clone-{Guid.NewGuid():N}@example.com";
        var client = enabledFactory.CreateCookieClient();
        await client.RegisterAsync(email, "Moonlight!Story42", cancellationToken);
        await using var scope = enabledFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var ownerId = await db.Users
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync(cancellationToken);
        db.CharacterProfiles.Add(CharacterProfile.Create(
            profileId,
            ownerId,
            "褚冥漾",
            avatarRelativePath: null,
            age: null,
            gender: null,
            birthday: null,
            personality: null,
            catchphrase: null,
            background: null,
            speakingStyle: null,
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        return client;
    }

    private static async Task<CreatedCharacter> CreateSeriesCharacterAsync(
        HttpClient owner,
        Guid? profileId,
        CancellationToken cancellationToken)
    {
        using var createSeries = await owner.PostWithCsrfAsync(
            "/api/series",
            new
            {
                name = $"本機克隆試音 {Guid.NewGuid():N}",
                narratorProvider = "local-clone",
                narratorVoice = await owner.ManagedVoiceAsync(cancellationToken),
                narratorRate = "-5%",
                narratorPitch = "+0Hz",
                narratorVolume = "+0%",
                defaultSpeakerPauseMs = 350,
            },
            cancellationToken);
        var series = await createSeries.Content
            .ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createSeries.StatusCode);
        Assert.NotNull(series);

        using var addCharacter = await owner.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = "褚冥漾",
                role = "Main",
                voiceProvider = "local-clone",
                voice = await owner.ManagedVoiceAsync(cancellationToken),
                rate = "-8%",
                pitch = "+2Hz",
                volume = "-3%",
                notes = (string?)null,
                characterProfileId = profileId,
            },
            cancellationToken);
        var withCharacter = await addCharacter.Content
            .ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, addCharacter.StatusCode);
        var character = Assert.Single(Assert.IsType<StorySeriesDetailsResponse>(withCharacter).Characters);
        return new CreatedCharacter(series.Id, character.Id);
    }

    private async Task<string> WriteAssetsAsync(
        Guid profileId,
        byte[] reference,
        string transcript,
        CancellationToken cancellationToken)
    {
        var assetRoot = Path.Combine(factory.StorageRoot, "local-clone", profileId.ToString("N"));
        Directory.CreateDirectory(assetRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(assetRoot, "reference.wav"),
            reference,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(assetRoot, "transcript.txt"),
            transcript,
            new UTF8Encoding(false),
            cancellationToken);
        return assetRoot;
    }

    private static async Task<FormalDbState> ReadFormalDbStateAsync(
        WebApplicationFactory<Program> enabledFactory,
        CreatedCharacter created,
        CancellationToken cancellationToken)
    {
        await using var scope = enabledFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var seriesPointer = await db.StorySeries
            .AsNoTracking()
            .Where(series => series.Id == created.SeriesId)
            .Select(series => series.ActiveCastRevisionId)
            .SingleAsync(cancellationToken);
        var character = await db.SeriesCharacters
            .AsNoTracking()
            .Where(candidate => candidate.Id == created.CharacterId)
            .Select(candidate => new
            {
                candidate.CharacterProfileId,
                candidate.VoiceProvider,
                candidate.Voice,
            })
            .SingleAsync(cancellationToken);
        return new FormalDbState(
            seriesPointer,
            character.CharacterProfileId,
            character.VoiceProvider,
            character.Voice,
            await db.NarrationJobs.CountAsync(cancellationToken),
            await db.NarrationCastRevisions.CountAsync(cancellationToken));
    }

    private static async Task<string?> ReadProblemCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        return body.RootElement.GetProperty("code").GetString();
    }

    private static string PreviewPath(Guid seriesId, Guid characterId) =>
        $"/api/series/{seriesId}/characters/{characterId}/local-clone-preview";

    private static string ProfilePreviewPath(Guid characterProfileId) =>
        $"/api/character-profiles/{characterProfileId}/local-clone-preview";

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static byte[] CreatePcmWave(int sampleRate, int durationSeconds)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        const ushort blockAlign = 2;
        var dataLength = checked(sampleRate * durationSeconds * blockAlign);
        var content = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(content);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(4), checked((uint)(content.Length - 8)));
        "WAVEfmt "u8.CopyTo(content.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(22), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(24), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(28), checked((uint)(sampleRate * blockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(34), bitsPerSample);
        "data"u8.CopyTo(content.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(40), checked((uint)dataLength));
        return content;
    }

    private sealed class FakeLocalCloneGatewayClient(byte[] content) : ILocalCloneGatewayClient
    {
        public int Calls { get; private set; }

        public LocalCloneGatewayRequest? LastRequest { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<LocalCloneGatewayAudio> SynthesizeAsync(
            LocalCloneGatewayRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(new LocalCloneGatewayAudio(content, "audio/wav"));
        }
    }

    private sealed record CreatedCharacter(Guid SeriesId, Guid CharacterId);

    private sealed record FormalDbState(
        Guid? ActiveCastRevisionId,
        Guid? CharacterProfileId,
        string VoiceProvider,
        string Voice,
        int NarrationJobCount,
        int CastRevisionCount);

}

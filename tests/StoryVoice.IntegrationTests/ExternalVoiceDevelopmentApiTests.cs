using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryVoice.Domain.Characters;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.ExternalVoices;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class ExternalVoiceDevelopmentApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Alias = "private-synthetic-voice";
    private const string KeyId = "dev_test_01";
    private const string ProjectId = "private-development-project";

    [Fact]
    public async Task Private_development_api_works_without_catalog_and_token_cannot_publish_it()
    {
        await using var fixture = await CreateFixtureAsync(DevelopmentState.Active);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var speech = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "private development sample"),
            cancellationToken);
        using var catalogRequest = new HttpRequestMessage(HttpMethod.Get, "/api/public/v1/voices");
        catalogRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
        using var catalog = await client.SendAsync(catalogRequest, cancellationToken);
        using var demo = await client.GetAsync(
            $"/api/public/v1/voices/{Alias}/demo",
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, speech.StatusCode);
        Assert.Equal(fixture.Output, await speech.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, demo.StatusCode);
        Assert.Equal(1, fixture.Gateway.Calls);
    }

    [Theory]
    [InlineData(DevelopmentState.GrantTampered)]
    [InlineData(DevelopmentState.Expired)]
    [InlineData(DevelopmentState.OwnerMismatch)]
    [InlineData(DevelopmentState.ProfileMismatch)]
    [InlineData(DevelopmentState.ReferenceBindingMismatch)]
    [InlineData(DevelopmentState.TranscriptBindingMismatch)]
    [InlineData(DevelopmentState.ReferenceAssetTampered)]
    [InlineData(DevelopmentState.TranscriptAssetTampered)]
    [InlineData(DevelopmentState.ProjectMismatch)]
    [InlineData(DevelopmentState.CommercialSchema)]
    [InlineData(DevelopmentState.WrongOrigin)]
    [InlineData(DevelopmentState.OperatorRevoked)]
    [InlineData(DevelopmentState.InactiveProfile)]
    public async Task Invalid_private_development_chain_is_hidden_before_gpu(
        DevelopmentState state)
    {
        await using var fixture = await CreateFixtureAsync(state);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "private development sample"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("voice_not_available", await ReadProblemCodeAsync(response, cancellationToken));
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task Private_development_tier_rejects_commercial_token_prefix_even_when_hash_matches()
    {
        await using var fixture = await CreateFixtureAsync(DevelopmentState.CommercialTokenPrefix);
        using var client = fixture.Factory.CreateClient();
        using var response = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "private development sample"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task Unknown_bearer_flood_is_limited_before_managed_credential_authentication()
    {
        await using var fixture = await CreateFixtureAsync(
            DevelopmentState.Active,
            preAuthenticationRequestsPerMinute: 2,
            preAuthenticationGlobalRequestsPerMinute: 10);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var unknownToken = $"svd1.unknown_credential_01.{new string('a', 43)}";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var unauthorized = await client.SendAsync(
                CreateSpeechRequest(unknownToken, "pre-authentication guard"),
                cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal("invalid_api_key", await ReadProblemCodeAsync(unauthorized, cancellationToken));
        }

        using var limited = await client.SendAsync(
            CreateSpeechRequest(unknownToken, "pre-authentication guard"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await ReadProblemCodeAsync(limited, cancellationToken));
        Assert.True(limited.Headers.CacheControl?.Private);
        Assert.True(limited.Headers.CacheControl?.NoStore);
        Assert.Equal("no-cache", string.Join(",", limited.Headers.Pragma.Select(value => value.Name)));
        Assert.Equal("nosniff", limited.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.InRange(
            int.Parse(
                limited.Headers.GetValues("Retry-After").Single(),
                CultureInfo.InvariantCulture),
            1,
            60);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    private async Task<DevelopmentFixture> CreateFixtureAsync(
        DevelopmentState state,
        int? preAuthenticationRequestsPerMinute = null,
        int? preAuthenticationGlobalRequestsPerMinute = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var assetRoot = Path.Combine(
            factory.StorageRoot,
            "external-development-api",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(assetRoot, "voice"));
        Directory.CreateDirectory(Path.Combine(assetRoot, "evidence"));

        var now = DateTimeOffset.UtcNow;
        var ownerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var reference = CreatePcmWave(48_000, 10);
        const string transcript = "帳號擁有者自行建立的純合成開發聲線逐字稿。";
        var referenceSha256 = Sha256(reference);
        var transcriptSha256 = CharacterVoiceTranscriptCanonicalizer.ComputeSha256Hex(transcript);
        var output = CreatePcmWave(24_000, 1);
        var grantEffective = state == DevelopmentState.Expired
            ? now.AddHours(-2)
            : now.AddMinutes(-30);
        var grantExpires = state == DevelopmentState.Expired
            ? now.AddHours(-1)
            : now.AddHours(1);

        byte[] grant;
        if (state == DevelopmentState.CommercialSchema)
        {
            grant = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = ExternalVoiceAuthorizationEvidenceValidator.CurrentSchema,
                grantId = "commercial-schema-confusion-01",
                consumerKeyId = KeyId,
                ownerId = ownerId.ToString("D"),
                voiceAlias = Alias,
                characterProfileId = profileId.ToString("D"),
                syntheticVoiceAuthorizationSha256 = new string('a', 64),
                effectiveAtUtc = UtcRoundtrip(grantEffective),
                expiresAtUtc = UtcRoundtrip(grantExpires),
                revokedAtUtc = (string?)null,
                projectId = ProjectId,
                consumerFamilyId = "commercial-family",
                territoryCountryCode = "TW",
                activation = new
                {
                    state = "active",
                    accountSubjectId = "acct_commercial_confusion",
                    auditEventId = "audit_commercial_confusion",
                    issuedAtUtc = UtcRoundtrip(grantEffective.AddMinutes(-1)),
                },
            });
        }
        else
        {
            grant = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = ExternalVoiceDevelopmentGrantValidator.CurrentSchema,
                grantId = "synthetic-development-grant-01",
                consumerKeyId = KeyId,
                ownerId = (state == DevelopmentState.OwnerMismatch
                    ? Guid.NewGuid()
                    : ownerId).ToString("D"),
                voiceAlias = Alias,
                characterProfileId = (state == DevelopmentState.ProfileMismatch
                    ? Guid.NewGuid()
                    : profileId).ToString("D"),
                referenceAudioSha256 = state == DevelopmentState.ReferenceBindingMismatch
                    ? new string('a', 64)
                    : referenceSha256,
                expectedTranscriptCanonicalSha256 =
                    state == DevelopmentState.TranscriptBindingMismatch
                        ? new string('b', 64)
                        : transcriptSha256,
                projectId = state == DevelopmentState.ProjectMismatch
                    ? "different-development-project"
                    : ProjectId,
                effectiveAtUtc = UtcRoundtrip(grantEffective),
                expiresAtUtc = UtcRoundtrip(grantExpires),
                revokedAtUtc = (string?)null,
                origin = state == DevelopmentState.WrongOrigin
                    ? "unknown-or-human-derived"
                    : ExternalVoiceDevelopmentGrantValidator.RequiredOrigin,
            });
        }

        var referencePath = Path.Combine(assetRoot, "voice", "reference.wav");
        var transcriptPath = Path.Combine(assetRoot, "voice", "transcript.txt");
        var grantPath = Path.Combine(assetRoot, "evidence", "development-grant.json");
        await File.WriteAllBytesAsync(
            referencePath,
            state == DevelopmentState.ReferenceAssetTampered
                ? [.. reference, (byte)0]
                : reference,
            cancellationToken);
        await File.WriteAllTextAsync(
            transcriptPath,
            state == DevelopmentState.TranscriptAssetTampered
                ? transcript + "竄改"
                : transcript,
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllBytesAsync(
            grantPath,
            state == DevelopmentState.GrantTampered
                ? [.. grant, (byte)' ']
                : grant,
            cancellationToken);

        var tokenPrefix = state == DevelopmentState.CommercialTokenPrefix
            ? ExternalVoiceAccessTiers.SubscriptionCommercialTokenPrefix
            : ExternalVoiceAccessTiers.PrivateDevelopmentTokenPrefix;
        var token = $"{tokenPrefix}{KeyId}.{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        var gateway = new FakeLocalCloneGatewayClient(output);
        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            var localPrefix = $"LocalClonePreview:AllowedProfiles:{profileId:D}";
            builder.UseSetting("LocalClonePreview:Enabled", "false");
            builder.UseSetting("LocalClonePreview:InternalToken", new string('t', 32));
            builder.UseSetting("LocalClonePreview:AssetRootPath", assetRoot);
            builder.UseSetting($"{localPrefix}:Label", "private synthetic development voice");
            builder.UseSetting($"{localPrefix}:ReferenceAudioRelativePath", "voice/reference.wav");
            builder.UseSetting($"{localPrefix}:TranscriptRelativePath", "voice/transcript.txt");
            builder.UseSetting($"{localPrefix}:ExpectedReferenceAudioSha256", referenceSha256);
            builder.UseSetting($"{localPrefix}:ExpectedTranscriptSha256", transcriptSha256);

            var consumerPrefix = $"ExternalVoiceApi:Consumers:{KeyId}";
            var voicePrefix = $"{consumerPrefix}:AllowedVoices:{Alias}";
            builder.UseSetting("ExternalVoiceApi:Enabled", "true");
            if (preAuthenticationRequestsPerMinute is not null)
            {
                builder.UseSetting(
                    "ExternalVoiceApi:PreAuthenticationRequestsPerMinute",
                    preAuthenticationRequestsPerMinute.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (preAuthenticationGlobalRequestsPerMinute is not null)
            {
                builder.UseSetting(
                    "ExternalVoiceApi:PreAuthenticationGlobalRequestsPerMinute",
                    preAuthenticationGlobalRequestsPerMinute.Value.ToString(CultureInfo.InvariantCulture));
            }

            builder.UseSetting(
                $"{consumerPrefix}:AccessTier",
                ExternalVoiceAccessTiers.PrivateDevelopment);
            builder.UseSetting($"{consumerPrefix}:DisplayName", "private development project");
            builder.UseSetting($"{consumerPrefix}:ProjectId", ProjectId);
            builder.UseSetting($"{consumerPrefix}:OwnerId", ownerId.ToString("D"));
            builder.UseSetting(
                $"{consumerPrefix}:TokenSha256",
                Sha256(Encoding.UTF8.GetBytes(token)));
            builder.UseSetting(
                $"{consumerPrefix}:EffectiveAtUtc",
                UtcRoundtrip(now.AddHours(-1)));
            builder.UseSetting(
                $"{consumerPrefix}:ExpiresAtUtc",
                UtcRoundtrip(now.AddHours(2)));
            builder.UseSetting(
                $"{voicePrefix}:AuthorizationEvidenceRelativePath",
                "evidence/development-grant.json");
            builder.UseSetting(
                $"{voicePrefix}:AuthorizationEvidenceSha256",
                Sha256(grant));
            if (state == DevelopmentState.OperatorRevoked)
            {
                builder.UseSetting($"{voicePrefix}:RevokedAtUtc", UtcRoundtrip(now));
            }

            builder.UseSetting("VoiceCatalog:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILocalCloneGatewayClient>();
                services.AddSingleton<ILocalCloneGatewayClient>(gateway);
            });
        });

        _ = configuredFactory.Services;
        await using (var scope = configuredFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var profile = CharacterProfile.Create(
                profileId,
                ownerId,
                "private synthetic character",
                avatarRelativePath: null,
                age: null,
                gender: null,
                birthday: null,
                personality: null,
                catchphrase: null,
                background: null,
                speakingStyle: null,
                now);
            if (state == DevelopmentState.InactiveProfile)
            {
                profile.Deactivate(now);
            }

            db.CharacterProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new DevelopmentFixture(configuredFactory, gateway, token, output);
    }

    private static HttpRequestMessage CreateSpeechRequest(string token, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/external/v1/speech")
        {
            Content = JsonContent.Create(new { voice = Alias, text }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            $"request_{Guid.NewGuid():N}");
        return request;
    }

    private static async Task<string?> ReadProblemCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("code").GetString();
    }

    private static string UtcRoundtrip(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Base64Url(ReadOnlySpan<byte> content) =>
        Convert.ToBase64String(content)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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

    public enum DevelopmentState
    {
        Active,
        GrantTampered,
        Expired,
        OwnerMismatch,
        ProfileMismatch,
        ReferenceBindingMismatch,
        TranscriptBindingMismatch,
        ReferenceAssetTampered,
        TranscriptAssetTampered,
        ProjectMismatch,
        CommercialSchema,
        WrongOrigin,
        OperatorRevoked,
        InactiveProfile,
        CommercialTokenPrefix,
    }

    private sealed class FakeLocalCloneGatewayClient(byte[] content) : ILocalCloneGatewayClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<LocalCloneGatewayAudio> SynthesizeAsync(
            LocalCloneGatewayRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new LocalCloneGatewayAudio(content, "audio/wav"));
        }
    }

    private sealed record DevelopmentFixture(
        WebApplicationFactory<Program> Factory,
        FakeLocalCloneGatewayClient Gateway,
        string Token,
        byte[] Output) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Factory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

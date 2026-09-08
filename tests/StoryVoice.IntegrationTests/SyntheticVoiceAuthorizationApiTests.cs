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
using Microsoft.Extensions.Options;
using StoryVoice.Application.VoiceCatalog;
using StoryVoice.Domain.Characters;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.ExternalVoices;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;
using StoryVoice.Infrastructure.VoiceCatalog;

namespace StoryVoice.IntegrationTests;

public sealed class SyntheticVoiceAuthorizationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Alias = "synthetic-story-voice";
    private const string KeyId = "synthetic_test_01";
    private const string ProjectId = "storyvoice-partner-test";
    private const string ConsumerFamilyId = "synthetic-application";
    private const string TerritoryCountryCode = "TW";

    [Fact]
    public async Task Public_detail_contains_only_public_fields_and_rechecks_assets_on_every_request()
    {
        await using var fixture = await CreateFixtureAsync(SyntheticState.Active);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.GetAsync($"/api/public/v1/voices/{Alias}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        var detail = await response.Content.ReadFromJsonAsync<PublicVoiceCatalogDetail>(cancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(Alias, detail.Voice.Alias);
        Assert.Equal($"/api/public/v1/voices/{Alias}/demo", detail.Voice.SampleUrl);
        Assert.True(detail.License.CommercialUseAllowed);
        Assert.True(detail.License.PublicDistributionAllowed);
        Assert.True(detail.License.CrossProjectApiAllowed);
        Assert.Equal("country-list", detail.License.TerritoryMode);
        Assert.Equal(["TW"], detail.License.TerritoryCountryCodes);
        Assert.True(detail.License.EffectiveAtUtc < DateTimeOffset.UtcNow);
        Assert.True(detail.License.ExpiresAtUtc > DateTimeOffset.UtcNow);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(["license", "voice"], body.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(
            new[] { "alias", "displayName", "subtitle", "disclosure", "styles", "useCases", "sampleUrl", "canPreview", "ctaKind", "subscriptionAvailable", "status" }.Order(),
            body.RootElement.GetProperty("voice").EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(
            new[] { "commercialUseAllowed", "publicDistributionAllowed", "crossProjectApiAllowed", "effectiveAtUtc", "expiresAtUtc", "territoryMode", "territoryCountryCodes" }.Order(),
            body.RootElement.GetProperty("license").EnumerateObject().Select(property => property.Name).Order());

        var options = fixture.Factory.Services.GetRequiredService<IOptions<VoiceCatalogOptions>>().Value;
        var demoPath = Path.Combine(options.AssetRootPath, options.Entries[Alias].DemoAudioRelativePath);
        await File.WriteAllBytesAsync(demoPath, [0], cancellationToken);
        using var removed = await client.GetAsync($"/api/public/v1/voices/{Alias}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task Valid_authorization_derives_catalog_and_commercial_api_bindings()
    {
        await using var fixture = await CreateFixtureAsync(SyntheticState.Active);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var catalog = await client.GetAsync("/api/public/v1/voices", cancellationToken);
        var cards = await catalog.Content.ReadFromJsonAsync<PublicVoiceCatalogCard[]>(cancellationToken);
        using var demo = await client.GetAsync(
            $"/api/public/v1/voices/{Alias}/demo",
            cancellationToken);
        using var speech = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "synthetic commercial sample"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        var card = Assert.Single(cards ?? []);
        Assert.Equal(Alias, card.Alias);
        Assert.Equal("Creator synthetic story character", card.DisplayName);
        Assert.Equal(["warm", "narrative"], card.Styles);
        Assert.Equal(HttpStatusCode.OK, demo.StatusCode);
        Assert.Equal(fixture.Demo, await demo.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(HttpStatusCode.OK, speech.StatusCode);
        Assert.Equal(fixture.Output, await speech.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(1, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task External_api_can_use_valid_private_voice_assets_while_public_catalog_is_disabled()
    {
        await using var fixture = await CreateFixtureAsync(
            SyntheticState.Active,
            publicCatalogEnabled: false);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var catalog = await client.GetAsync("/api/public/v1/voices", cancellationToken);
        using var demo = await client.GetAsync(
            $"/api/public/v1/voices/{Alias}/demo",
            cancellationToken);
        using var speech = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "headless synthetic API sample"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, demo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, speech.StatusCode);
        Assert.Equal(fixture.Output, await speech.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(1, fixture.Gateway.Calls);
    }

    [Theory]
    [InlineData(SyntheticState.AuthorizationTampered)]
    [InlineData(SyntheticState.ManifestTampered)]
    [InlineData(SyntheticState.DemoTampered)]
    public async Task Headless_external_api_still_fails_closed_before_gpu(
        SyntheticState state)
    {
        await using var fixture = await CreateFixtureAsync(
            state,
            publicCatalogEnabled: false);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var catalog = await client.GetAsync("/api/public/v1/voices", cancellationToken);
        using var speech = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "headless synthetic API sample"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, speech.StatusCode);
        Assert.Equal("voice_not_available", await ReadProblemCodeAsync(speech, cancellationToken));
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Theory]
    [InlineData(SyntheticState.AuthorizationTampered)]
    [InlineData(SyntheticState.AliasMismatch)]
    [InlineData(SyntheticState.ProfileMismatch)]
    [InlineData(SyntheticState.ReferenceBindingMismatch)]
    [InlineData(SyntheticState.NoHumanVoiceFlagFalse)]
    [InlineData(SyntheticState.ProviderRightsFalse)]
    [InlineData(SyntheticState.AuthorizationRevoked)]
    [InlineData(SyntheticState.AuthorizationExpired)]
    [InlineData(SyntheticState.ManifestTampered)]
    [InlineData(SyntheticState.ManifestMissing)]
    [InlineData(SyntheticState.TermsTampered)]
    [InlineData(SyntheticState.TermsMissing)]
    [InlineData(SyntheticState.DemoTampered)]
    [InlineData(SyntheticState.OfficialClaim)]
    public async Task Invalid_global_authorization_is_hidden_and_fails_before_gpu(
        SyntheticState state)
    {
        await using var fixture = await CreateFixtureAsync(state);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var catalog = await client.GetAsync("/api/public/v1/voices", cancellationToken);
        var cards = await catalog.Content.ReadFromJsonAsync<PublicVoiceCatalogCard[]>(cancellationToken);
        using var detail = await client.GetAsync($"/api/public/v1/voices/{Alias}", cancellationToken);
        using var demo = await client.GetAsync(
            $"/api/public/v1/voices/{Alias}/demo",
            cancellationToken);
        using var speech = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "synthetic commercial sample"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Empty(cards ?? []);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Contains("no-store", detail.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, demo.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, speech.StatusCode);
        Assert.Equal("voice_not_available", await ReadProblemCodeAsync(speech, cancellationToken));
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Theory]
    [InlineData(SyntheticState.UsageGrantTampered)]
    [InlineData(SyntheticState.UsageGrantDraft)]
    [InlineData(SyntheticState.UsageAuthorizationMismatch)]
    [InlineData(SyntheticState.UsageOwnerMismatch)]
    [InlineData(SyntheticState.UsageExpired)]
    [InlineData(SyntheticState.AuthorizationOwnerMismatch)]
    [InlineData(SyntheticState.DevelopmentGrantSchema)]
    public async Task Invalid_consumer_usage_is_rejected_without_hiding_catalog(
        SyntheticState state)
    {
        await using var fixture = await CreateFixtureAsync(state);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var catalog = await client.GetAsync("/api/public/v1/voices", cancellationToken);
        var cards = await catalog.Content.ReadFromJsonAsync<PublicVoiceCatalogCard[]>(cancellationToken);
        using var speech = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "synthetic commercial sample"),
            cancellationToken);

        Assert.Single(cards ?? []);
        Assert.Equal(HttpStatusCode.NotFound, speech.StatusCode);
        Assert.Equal("voice_not_available", await ReadProblemCodeAsync(speech, cancellationToken));
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task Request_contract_rejects_removed_purpose_field_before_gpu()
    {
        await using var fixture = await CreateFixtureAsync(SyntheticState.Active);
        using var client = fixture.Factory.CreateClient();
        using var request = CreateSpeechRequest(
            fixture.Token,
            new { voice = Alias, text = "sample", purpose = "subscription-commercial" });

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "invalid_request",
            await ReadProblemCodeAsync(response, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task External_endpoint_requires_its_scoped_bearer_token()
    {
        await using var fixture = await CreateFixtureAsync(SyntheticState.Active);
        using var client = fixture.Factory.CreateClient();
        using var request = CreateSpeechRequest(fixture.Token, "sample");
        request.Headers.Authorization = null;

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task Commercial_tier_rejects_private_development_token_prefix_even_when_hash_matches()
    {
        await using var fixture = await CreateFixtureAsync(
            SyntheticState.Active,
            tokenPrefix: ExternalVoiceAccessTiers.PrivateDevelopmentTokenPrefix);
        using var client = fixture.Factory.CreateClient();
        using var response = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "commercial prefix isolation sample"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task Idempotency_replays_and_rejects_payload_conflicts_without_extra_gpu_calls()
    {
        await using var fixture = await CreateFixtureAsync(SyntheticState.Active);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var idempotencyKey = $"request_{Guid.NewGuid():N}";

        using var first = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "same sample", idempotencyKey),
            cancellationToken);
        using var replay = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "same sample", idempotencyKey),
            cancellationToken);
        using var conflict = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "different sample", idempotencyKey),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", await ReadProblemCodeAsync(conflict, cancellationToken));
        Assert.Equal(1, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task Per_consumer_rate_limit_stops_the_fourth_request_before_gpu()
    {
        await using var fixture = await CreateFixtureAsync(SyntheticState.Active);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var responses = new List<HttpResponseMessage>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                responses.Add(await client.SendAsync(
                    CreateSpeechRequest(fixture.Token, $"sample {index}"),
                    cancellationToken));
            }

            Assert.All(responses.Take(3), response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(HttpStatusCode.TooManyRequests, responses[3].StatusCode);
            Assert.Equal(3, fixture.Gateway.Calls);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Operator_revocation_switch_hides_api_access_without_hiding_catalog()
    {
        await using var fixture = await CreateFixtureAsync(SyntheticState.OperatorRevoked);
        using var client = fixture.Factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var catalog = await client.GetAsync("/api/public/v1/voices", cancellationToken);
        var cards = await catalog.Content.ReadFromJsonAsync<PublicVoiceCatalogCard[]>(cancellationToken);
        using var speech = await client.SendAsync(
            CreateSpeechRequest(fixture.Token, "synthetic commercial sample"),
            cancellationToken);

        Assert.Single(cards ?? []);
        Assert.Equal(HttpStatusCode.NotFound, speech.StatusCode);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    private async Task<SyntheticFixture> CreateFixtureAsync(
        SyntheticState state,
        bool publicCatalogEnabled = true,
        string tokenPrefix = ExternalVoiceAccessTiers.SubscriptionCommercialTokenPrefix)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var assetRoot = Path.Combine(
            factory.StorageRoot,
            "synthetic-voice-authorization",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(assetRoot, "voice"));
        Directory.CreateDirectory(Path.Combine(assetRoot, "evidence"));
        Directory.CreateDirectory(Path.Combine(assetRoot, "demos"));

        var now = DateTimeOffset.UtcNow;
        var ownerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var reference = CreatePcmWave(48_000, 10);
        const string transcript = "由帳號擁有者自行建立的合成聲音逐字稿。";
        var referenceSha256 = Sha256(reference);
        var transcriptSha256 = CharacterVoiceTranscriptCanonicalizer.ComputeSha256Hex(transcript);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider = "self-hosted-generator",
            request = "controlled synthetic generation input",
            output = referenceSha256,
        });
        var terms = Encoding.UTF8.GetBytes(
            "Synthetic generator terms snapshot for commercial output use.");
        var manifestSha256 = Sha256(manifest);
        var termsSha256 = Sha256(terms);
        var demo = CreatePcmWave(24_000, 1);
        var output = CreatePcmWave(24_000, 1);
        var demoSha256 = Sha256(demo);

        var authorizationEffective = TruncateToSecond(
            state == SyntheticState.AuthorizationExpired ? now.AddHours(-3) : now.AddHours(-1));
        var authorizationExpires = TruncateToSecond(
            state == SyntheticState.AuthorizationExpired ? now.AddHours(-2) : now.AddHours(2));
        var createdAt = authorizationEffective.AddHours(-3);
        var termsAcceptedAt = createdAt.AddHours(-1);
        var attestedAt = authorizationEffective.AddHours(-2);
        var issuedAt = authorizationEffective.AddHours(-1);
        var revocationTimestamp = state == SyntheticState.AuthorizationRevoked
            ? UtcSeconds(authorizationEffective.AddMinutes(10))
            : null;
        var authorization = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = SyntheticVoiceAuthorizationValidator.CurrentSchema,
            authorizationId = "synthetic-authorization-test-01",
            ownerId = (state == SyntheticState.AuthorizationOwnerMismatch
                ? Guid.NewGuid()
                : ownerId).ToString("D"),
            voice = new
            {
                alias = state == SyntheticState.AliasMismatch ? "different-voice" : Alias,
                characterProfileId = (state == SyntheticState.ProfileMismatch
                    ? Guid.NewGuid()
                    : profileId).ToString("D"),
                displayName = state == SyntheticState.OfficialClaim
                    ? "Official synthetic character"
                    : "Creator synthetic story character",
                attributionText = "Created by the account owner",
                attributionDisplayAllowed = true,
                aiDisclosureRequired = true,
                styles = new[] { "warm", "narrative" },
                useCases = new[] { "audiobook", "cross-project-api" },
                fixedDemoSha256 = demoSha256,
                fixedDemoMediaType = "audio/wav",
            },
            creation = new
            {
                providerId = "self-hosted-generator",
                toolId = "storyvoice-synthetic-tool",
                modelId = "synthetic-model-test",
                modelRevision = "revision-test-01",
                createdAtUtc = UtcSeconds(createdAt),
                generationManifestSha256 = manifestSha256,
                licenseIdentifier = "commercial-output-terms-test",
                termsUri = "https://example.test/synthetic-terms",
                termsSnapshotSha256 = termsSha256,
                termsAcceptedAtUtc = UtcSeconds(termsAcceptedAt),
            },
            assetBindings = new
            {
                referenceAudioSha256 = state == SyntheticState.ReferenceBindingMismatch
                    ? new string('f', 64)
                    : referenceSha256,
                expectedTranscriptCanonicalSha256 = transcriptSha256,
            },
            sourceClaims = new
            {
                allGenerationInputsOwnedOrLicensed = true,
                noHumanVoiceInputProvided = state != SyntheticState.NoHumanVoiceFlagFalse,
                noHumanBiometricTemplateProvided = true,
                noIdentifiablePersonImitationRequested = true,
                noKnownIdentifiablePersonImitated = true,
                noThirdPartyCharacterOrBrandClaimed = true,
            },
            providerRights = new
            {
                commercialOutputUseAllowed = state != SyntheticState.ProviderRightsFalse,
                publicOutputDistributionAllowed = true,
                apiServiceUseAllowed = true,
                voiceModelDerivationAllowed = true,
            },
            permissions = new
            {
                catalogDisplay = true,
                demoPlayback = true,
                crossProjectApi = true,
                subscriptionOffering = true,
                commercialUse = true,
                publicDistribution = true,
            },
            allowedConsumerFamilies = new[] { ConsumerFamilyId },
            territory = new
            {
                mode = "country-list",
                countryCodes = new[] { TerritoryCountryCode },
            },
            externalProviderPolicy = new
            {
                mode = "prohibited",
                allowedProviderIds = Array.Empty<string>(),
            },
            effectiveAtUtc = UtcSeconds(authorizationEffective),
            expiresAtUtc = UtcSeconds(authorizationExpires),
            revocation = new
            {
                scope = SyntheticVoiceAuthorizationValidator.RequiredRevocationScope,
                contact = "owner@example.test",
                process = "Disable every dependent use and remove public access immediately.",
                requestedAtUtc = revocationTimestamp,
                effectiveAtUtc = revocationTimestamp,
            },
            attestation = new
            {
                state = "active",
                method = "authenticated-owner-action",
                accountSubjectId = "acct_synthetic_owner_01",
                auditEventId = "audit_synthetic_owner_01",
                attestedAtUtc = UtcSeconds(attestedAt),
                issuedAtUtc = UtcSeconds(issuedAt),
            },
        });
        var authorizationSha256 = Sha256(authorization);

        var usageEffective = state == SyntheticState.UsageExpired
            ? now.AddHours(-2)
            : now.AddMinutes(-30);
        var usageExpires = state == SyntheticState.UsageExpired
            ? now.AddHours(-1)
            : now.AddHours(1);
        var usage = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = state == SyntheticState.DevelopmentGrantSchema
                ? ExternalVoiceDevelopmentGrantValidator.CurrentSchema
                : ExternalVoiceAuthorizationEvidenceValidator.CurrentSchema,
            grantId = "synthetic-usage-test-01",
            consumerKeyId = KeyId,
            ownerId = (state == SyntheticState.UsageOwnerMismatch
                ? Guid.NewGuid()
                : ownerId).ToString("D"),
            voiceAlias = Alias,
            characterProfileId = profileId.ToString("D"),
            syntheticVoiceAuthorizationSha256 = state == SyntheticState.UsageAuthorizationMismatch
                ? new string('a', 64)
                : authorizationSha256,
            projectId = ProjectId,
            consumerFamilyId = ConsumerFamilyId,
            territoryCountryCode = TerritoryCountryCode,
            effectiveAtUtc = usageEffective.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            expiresAtUtc = usageExpires.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            revokedAtUtc = (string?)null,
            activation = new
            {
                state = state == SyntheticState.UsageGrantDraft ? "draft" : "active",
                accountSubjectId = "acct_synthetic_owner_01",
                auditEventId = "audit_synthetic_usage_01",
                issuedAtUtc = usageEffective.AddMinutes(-1).ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture),
            },
        });

        var referencePath = Path.Combine(assetRoot, "voice", "reference.wav");
        var transcriptPath = Path.Combine(assetRoot, "voice", "transcript.txt");
        var authorizationPath = Path.Combine(assetRoot, "evidence", "synthetic-authorization.json");
        var manifestPath = Path.Combine(assetRoot, "evidence", "generation-manifest.json");
        var termsPath = Path.Combine(assetRoot, "evidence", "terms-snapshot.txt");
        var usagePath = Path.Combine(assetRoot, "evidence", "api-usage-grant.json");
        var demoPath = Path.Combine(assetRoot, "demos", "fixed-demo.wav");

        await File.WriteAllBytesAsync(referencePath, reference, cancellationToken);
        await File.WriteAllTextAsync(
            transcriptPath,
            transcript,
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllBytesAsync(
            authorizationPath,
            state == SyntheticState.AuthorizationTampered
                ? [.. authorization, (byte)' ']
                : authorization,
            cancellationToken);
        if (state != SyntheticState.ManifestMissing)
        {
            await File.WriteAllBytesAsync(
                manifestPath,
                state == SyntheticState.ManifestTampered ? [.. manifest, (byte)' '] : manifest,
                cancellationToken);
        }

        if (state != SyntheticState.TermsMissing)
        {
            await File.WriteAllBytesAsync(
                termsPath,
                state == SyntheticState.TermsTampered ? [.. terms, (byte)' '] : terms,
                cancellationToken);
        }

        await File.WriteAllBytesAsync(
            usagePath,
            state == SyntheticState.UsageGrantTampered ? [.. usage, (byte)' '] : usage,
            cancellationToken);
        await File.WriteAllBytesAsync(
            demoPath,
            state == SyntheticState.DemoTampered ? [.. demo, (byte)0] : demo,
            cancellationToken);

        var tokenSecret = RandomNumberGenerator.GetBytes(32);
        var token = $"{tokenPrefix}{KeyId}.{Base64Url(tokenSecret)}";
        CryptographicOperations.ZeroMemory(tokenSecret);
        var tokenSha256 = Sha256(Encoding.UTF8.GetBytes(token));
        var gateway = new FakeLocalCloneGatewayClient(output);

        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            var localPrefix = $"LocalClonePreview:AllowedProfiles:{profileId:D}";
            builder.UseSetting("LocalClonePreview:Enabled", "false");
            builder.UseSetting("LocalClonePreview:InternalToken", new string('t', 32));
            builder.UseSetting("LocalClonePreview:AssetRootPath", assetRoot);
            builder.UseSetting($"{localPrefix}:Label", "synthetic private voice");
            builder.UseSetting($"{localPrefix}:ReferenceAudioRelativePath", "voice/reference.wav");
            builder.UseSetting($"{localPrefix}:TranscriptRelativePath", "voice/transcript.txt");
            builder.UseSetting($"{localPrefix}:ExpectedReferenceAudioSha256", referenceSha256);
            builder.UseSetting($"{localPrefix}:ExpectedTranscriptSha256", transcriptSha256);

            var consumerPrefix = $"ExternalVoiceApi:Consumers:{KeyId}";
            var apiGrantPrefix = $"{consumerPrefix}:AllowedVoices:{Alias}";
            builder.UseSetting("ExternalVoiceApi:Enabled", "true");
            builder.UseSetting(
                $"{consumerPrefix}:AccessTier",
                ExternalVoiceAccessTiers.SubscriptionCommercial);
            builder.UseSetting($"{consumerPrefix}:DisplayName", "synthetic test project");
            builder.UseSetting($"{consumerPrefix}:ProjectId", ProjectId);
            builder.UseSetting($"{consumerPrefix}:ConsumerFamilyId", ConsumerFamilyId);
            builder.UseSetting($"{consumerPrefix}:TerritoryCountryCode", TerritoryCountryCode);
            builder.UseSetting($"{consumerPrefix}:OwnerId", ownerId.ToString("D"));
            builder.UseSetting($"{consumerPrefix}:TokenSha256", tokenSha256);
            builder.UseSetting(
                $"{consumerPrefix}:EffectiveAtUtc",
                now.AddHours(-1).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            builder.UseSetting(
                $"{consumerPrefix}:ExpiresAtUtc",
                now.AddHours(2).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            builder.UseSetting(
                $"{apiGrantPrefix}:AuthorizationEvidenceRelativePath",
                "evidence/api-usage-grant.json");
            builder.UseSetting(
                $"{apiGrantPrefix}:AuthorizationEvidenceSha256",
                Sha256(usage));
            if (state == SyntheticState.OperatorRevoked)
            {
                builder.UseSetting(
                    $"{apiGrantPrefix}:RevokedAtUtc",
                    now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }

            var catalogPrefix = $"VoiceCatalog:Entries:{Alias}";
            builder.UseSetting(
                "VoiceCatalog:Enabled",
                publicCatalogEnabled ? "true" : "false");
            builder.UseSetting("VoiceCatalog:AssetRootPath", assetRoot);
            builder.UseSetting(
                $"{catalogPrefix}:SyntheticVoiceAuthorizationRelativePath",
                "evidence/synthetic-authorization.json");
            builder.UseSetting(
                $"{catalogPrefix}:SyntheticVoiceAuthorizationSha256",
                authorizationSha256);
            builder.UseSetting(
                $"{catalogPrefix}:GenerationManifestRelativePath",
                "evidence/generation-manifest.json");
            builder.UseSetting(
                $"{catalogPrefix}:TermsSnapshotRelativePath",
                "evidence/terms-snapshot.txt");
            builder.UseSetting($"{catalogPrefix}:DemoAudioRelativePath", "demos/fixed-demo.wav");
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
            db.CharacterProfiles.Add(CharacterProfile.Create(
                profileId,
                ownerId,
                "synthetic character",
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
        }

        return new SyntheticFixture(configuredFactory, gateway, token, demo, output);
    }

    private static HttpRequestMessage CreateSpeechRequest(
        string token,
        string text,
        string? idempotencyKey = null) =>
        CreateSpeechRequest(token, new { voice = Alias, text }, idempotencyKey);

    private static HttpRequestMessage CreateSpeechRequest(
        string token,
        object body,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/external/v1/speech")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            idempotencyKey ?? $"request_{Guid.NewGuid():N}");
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

    private static string Base64Url(ReadOnlySpan<byte> content) =>
        Convert.ToBase64String(content)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, TimeSpan.Zero);

    private static string UtcSeconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

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

    public enum SyntheticState
    {
        Active,
        AuthorizationTampered,
        AliasMismatch,
        ProfileMismatch,
        ReferenceBindingMismatch,
        NoHumanVoiceFlagFalse,
        ProviderRightsFalse,
        AuthorizationRevoked,
        AuthorizationExpired,
        ManifestTampered,
        ManifestMissing,
        TermsTampered,
        TermsMissing,
        DemoTampered,
        OfficialClaim,
        UsageGrantTampered,
        UsageGrantDraft,
        UsageAuthorizationMismatch,
        UsageOwnerMismatch,
        UsageExpired,
        AuthorizationOwnerMismatch,
        OperatorRevoked,
        DevelopmentGrantSchema,
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

    private sealed record SyntheticFixture(
        WebApplicationFactory<Program> Factory,
        FakeLocalCloneGatewayClient Gateway,
        string Token,
        byte[] Demo,
        byte[] Output) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Factory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

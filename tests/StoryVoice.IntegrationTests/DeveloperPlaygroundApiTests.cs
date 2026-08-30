using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;
using StoryVoice.Infrastructure.Identity;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class DeveloperPlaygroundApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string ConsumerKeyId = "playground_project_01";
    private const string ProjectId = "playground-project";
    private const string VoiceAlias = "private-synthetic-voice";
    private const string Password = "Moonlight!Story42";
    private const string InputText = "這段文字只用來產生聲音";
    private const string Secret = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly string AccessToken = $"svd1.{ConsumerKeyId}.{Secret}";

    [Fact]
    public async Task Playground_is_owner_scoped_csrf_protected_and_records_safe_usage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        using var configuredFactory = CreateConfiguredFactory(ownerId, DateTimeOffset.UtcNow);
        using var anonymousClient = configuredFactory.CreateClient();
        using var anonymous = await anonymousClient.PostAsJsonAsync(
            "/api/developer/external-voice/playground",
            CreateRequest(InputText, "playground-anonymous-0001"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var ownerClient = await CreateOwnerClientAsync(
            configuredFactory,
            ownerId,
            cancellationToken);
        using var missingCsrf = await ownerClient.PostAsJsonAsync(
            "/api/developer/external-voice/playground",
            CreateRequest(InputText, "playground-no-csrf-0001"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        using var success = await ownerClient.PostWithCsrfAsync(
            "/api/developer/external-voice/playground",
            CreateRequest(InputText, "playground-success-0001"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal("audio/wav", success.Content.Headers.ContentType?.MediaType);
        Assert.Equal(64, (await success.Content.ReadAsByteArrayAsync(cancellationToken)).Length);
        Assert.Equal("no-store", success.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", success.Headers.GetValues("X-Content-Type-Options").Single());
        var requestId = success.Headers.GetValues("X-StoryVoice-Request-Id").Single();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        Assert.Equal("1250", success.Headers.GetValues("X-StoryVoice-Audio-Duration-Ms").Single());
        Assert.True(int.Parse(
            success.Headers.GetValues("X-StoryVoice-Latency-Ms").Single(),
            CultureInfo.InvariantCulture) >= 0);

        await WaitForUsageCountAsync(configuredFactory, ownerId, 1, cancellationToken);
        await using (var scope = configuredFactory.Services.CreateAsyncScope())
        {
            var usage = await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>()
                .ExternalVoiceUsageRecords
                .SingleAsync(record => record.RequestId == requestId, cancellationToken);
            Assert.Equal(ownerId, usage.OwnerId);
            Assert.Equal(ConsumerKeyId, usage.ConsumerKeyId);
            Assert.Equal("owner-session-playground", usage.CredentialKeyId);
            Assert.Equal(ProjectId, usage.ProjectId);
            Assert.Equal(VoiceAlias, usage.VoiceAlias);
            Assert.Equal(ExternalVoiceUsageOutcomes.Succeeded, usage.Outcome);
            Assert.Equal(InputText.EnumerateRunes().Count(), usage.TextCharacters);
            Assert.Equal(64, usage.ResponseBytes);
            Assert.Equal(1_250, usage.AudioDurationMilliseconds);
        }

        using var otherOwnerClient = await configuredFactory.CreateAuthenticatedClientAsync(cancellationToken);
        using var hidden = await otherOwnerClient.PostWithCsrfAsync(
            "/api/developer/external-voice/playground",
            CreateRequest(InputText, "playground-other-owner-0001"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.DoesNotContain(ownerId.ToString("D"), await hidden.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public async Task Playground_returns_stable_failures_and_enforces_session_rate_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        using var configuredFactory = CreateConfiguredFactory(ownerId, DateTimeOffset.UtcNow);
        using var ownerClient = await CreateOwnerClientAsync(
            configuredFactory,
            ownerId,
            cancellationToken);

        await AssertProblemAsync(
            ownerClient,
            "conflict",
            "playground-conflict-0001",
            HttpStatusCode.Conflict,
            "idempotency_conflict",
            null,
            cancellationToken);
        await AssertProblemAsync(
            ownerClient,
            "limited",
            "playground-limited-0001",
            HttpStatusCode.TooManyRequests,
            "rate_limited",
            7,
            cancellationToken);
        await AssertProblemAsync(
            ownerClient,
            "unavailable",
            "playground-unavailable-0001",
            HttpStatusCode.ServiceUnavailable,
            "synthesis_unavailable",
            30,
            cancellationToken);

        using var fixedWindowLimited = await ownerClient.PostWithCsrfAsync(
            "/api/developer/external-voice/playground",
            CreateRequest(InputText, "playground-window-0001"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, fixedWindowLimited.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(
            fixedWindowLimited.Headers.GetValues("X-StoryVoice-Request-Id").Single()));

        await WaitForUsageCountAsync(configuredFactory, ownerId, 4, cancellationToken);
        await using var scope = configuredFactory.Services.CreateAsyncScope();
        var records = await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>()
            .ExternalVoiceUsageRecords
            .Where(record => record.OwnerId == ownerId)
            .OrderBy(record => record.OccurredAtUtc)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(4, records.Length);
        Assert.Equal(
            ["idempotency_conflict", "rate_limited", "synthesis_unavailable", "rate_limited"],
            records.Select(record => record.Outcome).ToArray());
        Assert.All(records, record => Assert.True(record.TextCharacters > 0));
    }

    [Fact]
    public async Task Playground_and_external_api_share_the_consumer_rate_limit_budget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        using var configuredFactory = CreateConfiguredFactory(
            ownerId,
            DateTimeOffset.UtcNow,
            requestsPerMinute: 1);
        using var ownerClient = await CreateOwnerClientAsync(
            configuredFactory,
            ownerId,
            cancellationToken);
        using var externalClient = configuredFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        using var externalRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/external/v1/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { voice = VoiceAlias, text = InputText }),
                Encoding.UTF8,
                "application/json"),
        };
        externalRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        externalRequest.Headers.Add("Idempotency-Key", "playground-shared-budget-0001");
        using var externalResponse = await externalClient.SendAsync(
            externalRequest,
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, externalResponse.StatusCode);

        using var playgroundResponse = await ownerClient.PostWithCsrfAsync(
            "/api/developer/external-voice/playground",
            CreateRequest(InputText, "playground-shared-budget-0002"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, playgroundResponse.StatusCode);
        using var problem = JsonDocument.Parse(
            await playgroundResponse.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal("rate_limited", problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task WaitForUsageCountAsync(
        WebApplicationFactory<Program> configuredFactory,
        Guid ownerId,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            await using var scope = configuredFactory.Services.CreateAsyncScope();
            var count = await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>()
                .ExternalVoiceUsageRecords
                .CountAsync(record => record.OwnerId == ownerId, cancellationToken);
            if (count >= expectedCount)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {expectedCount} background usage records; found {count}.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private WebApplicationFactory<Program> CreateConfiguredFactory(
        Guid ownerId,
        DateTimeOffset now,
        int requestsPerMinute = 3) =>
        factory.WithWebHostBuilder(builder =>
        {
            var profileId = Guid.NewGuid();
            var localPrefix = $"LocalClonePreview:AllowedProfiles:{profileId:D}";
            builder.UseSetting("LocalClonePreview:Enabled", "false");
            builder.UseSetting("LocalClonePreview:InternalToken", new string('t', 32));
            builder.UseSetting(
                "LocalClonePreview:AssetRootPath",
                Path.Combine(factory.StorageRoot, $"developer-playground-assets-{ownerId:N}"));
            builder.UseSetting($"{localPrefix}:Label", "playground test voice");
            builder.UseSetting($"{localPrefix}:ReferenceAudioRelativePath", "voice/reference.wav");
            builder.UseSetting($"{localPrefix}:TranscriptRelativePath", "voice/transcript.txt");
            builder.UseSetting($"{localPrefix}:ExpectedReferenceAudioSha256", new string('c', 64));
            builder.UseSetting($"{localPrefix}:ExpectedTranscriptSha256", new string('d', 64));

            var consumerPrefix = $"ExternalVoiceApi:Consumers:{ConsumerKeyId}";
            var voicePrefix = $"{consumerPrefix}:AllowedVoices:{VoiceAlias}";
            builder.UseSetting("ExternalVoiceApi:Enabled", "true");
            builder.UseSetting(
                "ExternalVoiceApi:RequestsPerMinute",
                requestsPerMinute.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting($"{consumerPrefix}:AccessTier", ExternalVoiceAccessTiers.PrivateDevelopment);
            builder.UseSetting($"{consumerPrefix}:DisplayName", "playground test project");
            builder.UseSetting($"{consumerPrefix}:ProjectId", ProjectId);
            builder.UseSetting($"{consumerPrefix}:OwnerId", ownerId.ToString("D"));
            builder.UseSetting(
                $"{consumerPrefix}:TokenSha256",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AccessToken)))
                    .ToLowerInvariant());
            builder.UseSetting(
                $"{consumerPrefix}:EffectiveAtUtc",
                now.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture));
            builder.UseSetting(
                $"{consumerPrefix}:ExpiresAtUtc",
                now.AddDays(29).ToString("O", CultureInfo.InvariantCulture));
            builder.UseSetting(
                $"{voicePrefix}:AuthorizationEvidenceRelativePath",
                "evidence/playground-test-grant.json");
            builder.UseSetting($"{voicePrefix}:AuthorizationEvidenceSha256", new string('b', 64));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExternalVoiceSynthesisService>();
                services.AddScoped<IExternalVoiceSynthesisService, FakeExternalVoiceSynthesisService>();
            });
        });

    private static async Task<HttpClient> CreateOwnerClientAsync(
        WebApplicationFactory<Program> configuredFactory,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var ownerEmail = $"playground-owner-{ownerId:N}@example.com";
        await using (var scope = configuredFactory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await userManager.CreateAsync(new ApplicationUser
            {
                Id = ownerId,
                UserName = ownerEmail,
                Email = ownerEmail,
            }, Password);
            Assert.True(created.Succeeded);
        }

        var client = configuredFactory.CreateCookieClient();
        using var login = await client.PostWithCsrfAsync(
            "/api/auth/login",
            new { email = ownerEmail, password = Password, rememberMe = false },
            cancellationToken);
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static object CreateRequest(string text, string idempotencyKey) => new
    {
        projectId = ProjectId,
        voice = VoiceAlias,
        text,
        idempotencyKey,
    };

    private static async Task AssertProblemAsync(
        HttpClient client,
        string text,
        string idempotencyKey,
        HttpStatusCode expectedStatus,
        string expectedCode,
        int? expectedRetryAfter,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostWithCsrfAsync(
            "/api/developer/external-voice/playground",
            CreateRequest(text, idempotencyKey),
            cancellationToken);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Single(response.Headers.GetValues("X-StoryVoice-Request-Id"));
        if (expectedRetryAfter is { } retryAfter)
        {
            Assert.Equal(
                retryAfter.ToString(CultureInfo.InvariantCulture),
                response.Headers.GetValues("Retry-After").Single());
        }

        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            problem.RootElement.GetProperty("requestId").GetString()));
        Assert.DoesNotContain(InputText, problem.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    private sealed class FakeExternalVoiceSynthesisService : IExternalVoiceSynthesisService
    {
        public Task<ExternalVoiceAudio> SynthesizeAsync(
            string consumerKeyId,
            ExternalVoiceSynthesisRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => request.Text switch
            {
                "conflict" => throw new ExternalVoiceSynthesisException(
                    ExternalVoiceSynthesisFailureKind.IdempotencyConflict),
                "limited" => throw new ExternalVoiceSynthesisException(
                    ExternalVoiceSynthesisFailureKind.RateLimited,
                    retryAfterSeconds: 7),
                "unavailable" => throw new ExternalVoiceSynthesisException(
                    ExternalVoiceSynthesisFailureKind.SynthesisUnavailable),
                _ => Task.FromResult(new ExternalVoiceAudio(new byte[64], "audio/wav", 1_250)),
            };
    }
}

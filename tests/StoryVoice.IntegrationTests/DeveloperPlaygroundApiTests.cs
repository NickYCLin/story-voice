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
using StackExchange.Redis;
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
    public async Task Unauthenticated_requests_share_source_limits_before_authentication_including_route_variants()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var redis = new RedisRateLimitFixture();
        await redis.InitializeAsync();
        using var firstConnection = await redis.ConnectAsync();
        using var secondConnection = await redis.ConnectAsync();
        var ownerId = Guid.NewGuid();
        using var first = CreateConfiguredFactory(ownerId, DateTimeOffset.UtcNow, sharedConnection: firstConnection,
            sharedPreAuthentication: true, preAuthenticationRequestsPerMinute: 2);
        using var second = CreateConfiguredFactory(ownerId, DateTimeOffset.UtcNow, sharedConnection: secondConnection,
            sharedPreAuthentication: true, preAuthenticationRequestsPerMinute: 2);
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        using var one = await firstClient.PostAsJsonAsync("/api/external/v1/speech", new { }, ct);
        using var two = await secondClient.PostAsJsonAsync("/api/external/v1/speech", new { }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, one.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, two.StatusCode);

        using var blocked = await firstClient.PostAsJsonAsync("/API/EXTERNAL/V1/SPEECH", new { }, ct);
        using var trailingSlash = await secondClient.PostAsJsonAsync("/api/external/v1/speech/", new { }, ct);
        foreach (var response in new[] { blocked, trailingSlash })
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain("aaaa", await response.Content.ReadAsStringAsync(ct));
        }

        using var healthy = await firstClient.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        var counter = await firstConnection.GetDatabase().StringGetAsync(RedisExternalVoiceSharedRateLimiter.PreAuthenticationGlobalKey);
        Assert.Equal("2", (string?)counter);
    }

    [Fact]
    public async Task Unavailable_shared_anonymous_limits_do_not_block_unrelated_routes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var configured = CreateConfiguredFactory(Guid.NewGuid(), DateTimeOffset.UtcNow, sharedPreAuthentication: true);
        using var client = configured.CreateClient();
        using var blocked = await client.PostAsJsonAsync("/api/external/v1/speech", new { }, ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), blocked.Headers.RetryAfter?.Delta);
        using var healthy = await client.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task Separate_api_instances_share_external_and_playground_limits_across_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var redis = new RedisRateLimitFixture();
        await redis.InitializeAsync();
        using var firstConnection = await redis.ConnectAsync();
        using var secondConnection = await redis.ConnectAsync();
        var ownerId = Guid.NewGuid();
        using var first = CreateConfiguredFactory(ownerId, DateTimeOffset.UtcNow, sharedConnection: firstConnection, sharedRateLimit: true);
        using var second = CreateConfiguredFactory(ownerId, DateTimeOffset.UtcNow, sharedConnection: secondConnection, sharedRateLimit: true);
        using var external = first.CreateClient();
        using var owner = await CreateOwnerClientAsync(second, ownerId, ct);

        using var anonymous = await external.PostAsJsonAsync("/api/external/v1/speech", new { voice = VoiceAlias, text = InputText }, ct);
        using var missingCsrf = await owner.PostAsJsonAsync("/api/developer/external-voice/playground", CreateRequest(InputText, "shared-missing-csrf-01"), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        using var one = await SendExternalAsync(external, ct);
        using var two = await owner.PostWithCsrfAsync("/api/developer/external-voice/playground", CreateRequest(InputText, "shared-budget-playground-01"), ct);
        using var three = await SendExternalAsync(external, ct);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        Assert.Equal(HttpStatusCode.OK, three.StatusCode);

        using var externalLimited = await SendExternalAsync(external, ct);
        using var playgroundLimited = await owner.PostWithCsrfAsync("/api/developer/external-voice/playground", CreateRequest(InputText, "shared-budget-playground-02"), ct);
        foreach (var response in new[] { externalLimited, playgroundLimited })
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
            Assert.Contains("rate_limited", await response.Content.ReadAsStringAsync(ct));
        }

        // This factory has no local request history but must retain the shared limit.
        using var restarted = CreateConfiguredFactory(ownerId, DateTimeOffset.UtcNow, sharedConnection: secondConnection, sharedRateLimit: true);
        using var restartedClient = restarted.CreateClient();
        using var afterRestart = await SendExternalAsync(restartedClient, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, afterRestart.StatusCode);
    }

    [Fact]
    public async Task Shared_limit_unavailability_stops_both_entry_points_before_synthesis()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        // Testing has no registered Redis connection. Enabled shared limits must fail closed.
        using var configured = CreateConfiguredFactory(ownerId, DateTimeOffset.UtcNow, sharedRateLimit: true);
        using var owner = await CreateOwnerClientAsync(configured, ownerId, ct);
        using var external = configured.CreateClient();
        using var externalUnavailable = await SendExternalAsync(external, ct);
        using var playgroundUnavailable = await owner.PostWithCsrfAsync("/api/developer/external-voice/playground", CreateRequest(InputText, "shared-unavailable-01"), ct);
        foreach (var response in new[] { externalUnavailable, playgroundUnavailable })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.Contains("synthesis_unavailable", body);
            Assert.DoesNotContain("Redis", body);
            Assert.DoesNotContain(InputText, body);
        }
    }

    private static async Task<HttpResponseMessage> SendExternalAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/external/v1/speech")
        {
            Content = JsonContent.Create(new { voice = VoiceAlias, text = InputText }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        request.Headers.Add("Idempotency-Key", $"shared-budget-{Guid.NewGuid():N}");
        return await client.SendAsync(request, cancellationToken);
    }

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
        int requestsPerMinute = 3,
        IConnectionMultiplexer? sharedConnection = null,
        bool sharedRateLimit = false,
        bool sharedPreAuthentication = false,
        int preAuthenticationRequestsPerMinute = 60) =>
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
            builder.UseSetting("ExternalVoiceApi:SharedRateLimitEnabled", sharedRateLimit.ToString());
            builder.UseSetting("ExternalVoiceApi:SharedPreAuthenticationRateLimitEnabled", sharedPreAuthentication.ToString());
            builder.UseSetting("ExternalVoiceApi:SharedPreAuthenticationHashKey", sharedPreAuthentication ? new string('a', 64) : string.Empty);
            builder.UseSetting("ExternalVoiceApi:PreAuthenticationRequestsPerMinute", preAuthenticationRequestsPerMinute.ToString(CultureInfo.InvariantCulture));
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
                if (sharedConnection is not null)
                {
                    services.AddSingleton(sharedConnection);
                }
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

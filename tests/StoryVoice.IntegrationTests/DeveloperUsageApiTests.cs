using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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

public sealed class DeveloperUsageApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string ConsumerKeyId = "usage_project_01";
    private const string ProjectId = "usage-project";
    private const string VoiceAlias = "private-synthetic-voice";
    private const string Password = "Moonlight!Story42";
    private const string Secret = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string InputText = "請勿保存這段文字";

    private static readonly string AccessToken = $"svd1.{ConsumerKeyId}.{Secret}";

    [Fact]
    public async Task Usage_is_durable_owner_scoped_filterable_and_omits_sensitive_request_material()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var configuredFactory = CreateConfiguredFactory(ownerId, now, requestsPerMinute: 3);
        using var ownerClient = await CreateOwnerClientAsync(configuredFactory, ownerId, cancellationToken);

        using var externalClient = configuredFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        using var success = await SendExternalRequestAsync(
            externalClient,
            JsonSerializer.Serialize(new { voice = VoiceAlias, text = InputText }),
            "usage-success-key-0001",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var successRequestId = success.Headers.GetValues("X-StoryVoice-Request-Id").Single();

        using var invalid = await SendExternalRequestAsync(
            externalClient,
            "{",
            "usage-invalid-key-0001",
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Single(invalid.Headers.GetValues("X-StoryVoice-Request-Id"));

        var fromUtc = now.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture);
        var toUtc = now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture);
        using var usageResponse = await ownerClient.GetAsync(
            $"/api/developer/external-voice/usage?fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}&limit=20",
            cancellationToken);
        usageResponse.EnsureSuccessStatusCode();
        Assert.Equal("no-store", usageResponse.Headers.CacheControl?.ToString());
        var payload = await usageResponse.Content.ReadAsStringAsync(cancellationToken);
        using var usage = JsonDocument.Parse(payload);
        var summary = usage.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("totalRequests").GetInt32());
        Assert.Equal(1, summary.GetProperty("successfulRequests").GetInt32());
        Assert.Equal(50, summary.GetProperty("successRatePercent").GetDouble());
        Assert.Equal(64, summary.GetProperty("outputBytes").GetInt64());
        Assert.Equal(1_250, summary.GetProperty("outputDurationMilliseconds").GetInt64());

        var activities = usage.RootElement.GetProperty("activities");
        Assert.Equal(2, activities.GetArrayLength());
        Assert.Contains(
            activities.EnumerateArray(),
            activity => activity.GetProperty("requestId").GetString() == successRequestId
                && activity.GetProperty("voiceAlias").GetString() == VoiceAlias
                && activity.GetProperty("outcome").GetString() == ExternalVoiceUsageOutcomes.Succeeded);
        Assert.Contains(
            activities.EnumerateArray(),
            activity => activity.GetProperty("outcome").GetString() == "invalid_request");
        Assert.DoesNotContain(InputText, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("usage-success-key-0001", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(ownerId.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);

        using var filteredResponse = await ownerClient.GetAsync(
            $"/api/developer/external-voice/usage?fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}&projectId={ProjectId}&voice={VoiceAlias}",
            cancellationToken);
        filteredResponse.EnsureSuccessStatusCode();
        using var filtered = JsonDocument.Parse(
            await filteredResponse.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal(1, filtered.RootElement.GetProperty("summary").GetProperty("totalRequests").GetInt32());

        using var otherOwnerClient = await configuredFactory.CreateAuthenticatedClientAsync(cancellationToken);
        using var otherResponse = await otherOwnerClient.GetAsync(
            $"/api/developer/external-voice/usage?fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}",
            cancellationToken);
        otherResponse.EnsureSuccessStatusCode();
        using var otherUsage = JsonDocument.Parse(
            await otherResponse.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal(0, otherUsage.RootElement.GetProperty("summary").GetProperty("totalRequests").GetInt32());

        using var anonymous = configuredFactory.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync(
            "/api/developer/external-voice/usage",
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        await using var scope = configuredFactory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>()
            .ExternalVoiceUsageRecords
            .OrderBy(record => record.OccurredAtUtc)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, stored.Length);
        Assert.All(stored, record =>
        {
            Assert.Equal(ownerId, record.OwnerId);
            Assert.Equal(ConsumerKeyId, record.ConsumerKeyId);
            Assert.Equal(ConsumerKeyId, record.CredentialKeyId);
            Assert.Equal(ProjectId, record.ProjectId);
        });
    }

    [Fact]
    public async Task Authenticated_rate_limit_rejection_is_written_to_the_usage_ledger()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var configuredFactory = CreateConfiguredFactory(ownerId, now, requestsPerMinute: 1);
        using var ownerClient = await CreateOwnerClientAsync(configuredFactory, ownerId, cancellationToken);
        using var externalClient = configuredFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        using var first = await SendExternalRequestAsync(
            externalClient,
            JsonSerializer.Serialize(new { voice = VoiceAlias, text = InputText }),
            "usage-rate-key-0001",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var limited = await SendExternalRequestAsync(
            externalClient,
            JsonSerializer.Serialize(new { voice = VoiceAlias, text = InputText }),
            "usage-rate-key-0002",
            cancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        var fromUtc = now.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture);
        var toUtc = now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture);
        using var response = await ownerClient.GetAsync(
            $"/api/developer/external-voice/usage?fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var usage = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var summary = usage.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("totalRequests").GetInt32());
        Assert.Equal(1, summary.GetProperty("rateLimitedRequests").GetInt32());
        Assert.Contains(
            usage.RootElement.GetProperty("activities").EnumerateArray(),
            activity => activity.GetProperty("statusCode").GetInt32() == 429
                && activity.GetProperty("outcome").GetString() == "rate_limited");
    }

    private WebApplicationFactory<Program> CreateConfiguredFactory(
        Guid ownerId,
        DateTimeOffset now,
        int requestsPerMinute) =>
        factory.WithWebHostBuilder(builder =>
        {
            var profileId = Guid.NewGuid();
            var localPrefix = $"LocalClonePreview:AllowedProfiles:{profileId:D}";
            builder.UseSetting("LocalClonePreview:Enabled", "false");
            builder.UseSetting("LocalClonePreview:InternalToken", new string('t', 32));
            builder.UseSetting(
                "LocalClonePreview:AssetRootPath",
                Path.Combine(factory.StorageRoot, $"developer-usage-assets-{ownerId:N}"));
            builder.UseSetting($"{localPrefix}:Label", "usage test voice");
            builder.UseSetting($"{localPrefix}:ReferenceAudioRelativePath", "voice/reference.wav");
            builder.UseSetting($"{localPrefix}:TranscriptRelativePath", "voice/transcript.txt");
            builder.UseSetting($"{localPrefix}:ExpectedReferenceAudioSha256", new string('c', 64));
            builder.UseSetting($"{localPrefix}:ExpectedTranscriptSha256", new string('d', 64));

            var consumerPrefix = $"ExternalVoiceApi:Consumers:{ConsumerKeyId}";
            var voicePrefix = $"{consumerPrefix}:AllowedVoices:{VoiceAlias}";
            builder.UseSetting("ExternalVoiceApi:Enabled", "true");
            builder.UseSetting("ExternalVoiceApi:RequestsPerMinute", requestsPerMinute.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting($"{consumerPrefix}:AccessTier", ExternalVoiceAccessTiers.PrivateDevelopment);
            builder.UseSetting($"{consumerPrefix}:DisplayName", "usage test project");
            builder.UseSetting($"{consumerPrefix}:ProjectId", ProjectId);
            builder.UseSetting($"{consumerPrefix}:OwnerId", ownerId.ToString("D"));
            builder.UseSetting($"{consumerPrefix}:TokenSha256", Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(AccessToken))).ToLowerInvariant());
            builder.UseSetting(
                $"{consumerPrefix}:EffectiveAtUtc",
                now.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture));
            builder.UseSetting(
                $"{consumerPrefix}:ExpiresAtUtc",
                now.AddDays(29).ToString("O", CultureInfo.InvariantCulture));
            builder.UseSetting($"{voicePrefix}:AuthorizationEvidenceRelativePath", "evidence/usage-test-grant.json");
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
        var ownerEmail = $"usage-owner-{ownerId:N}@example.com";
        await using (var scope = configuredFactory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = ownerId,
                UserName = ownerEmail,
                Email = ownerEmail,
            };
            var created = await userManager.CreateAsync(user, Password);
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

    private static async Task<HttpResponseMessage> SendExternalRequestAsync(
        HttpClient client,
        string body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/external/v1/speech")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request, cancellationToken);
    }

    private sealed class FakeExternalVoiceSynthesisService : IExternalVoiceSynthesisService
    {
        public Task<ExternalVoiceAudio> SynthesizeAsync(
            string consumerKeyId,
            ExternalVoiceSynthesisRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalVoiceAudio(new byte[64], "audio/wav", 1_250));
    }
}

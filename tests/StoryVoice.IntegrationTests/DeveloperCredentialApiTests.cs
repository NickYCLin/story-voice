using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryVoice.Domain.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;
using StoryVoice.Infrastructure.Identity;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class DeveloperCredentialApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string ConsumerKeyId = "credential_owner_project_01";
    private const string ProjectId = "credential-owner-project";
    private const string VoiceAlias = "private-synthetic-voice";
    private const string Password = "Moonlight!Story42";

    [Fact]
    public async Task Credential_lifecycle_is_owner_scoped_one_time_and_authenticates_without_storing_raw_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var configuredFactory = CreateConfiguredFactory(ownerId, now);
        var ownerEmail = $"credential-owner-{Guid.NewGuid():N}@example.com";
        await CreateUserAsync(configuredFactory, ownerId, ownerEmail);

        using var anonymousClient = configuredFactory.CreateClient();
        using var anonymousResponse = await anonymousClient.GetAsync(
            "/api/developer/external-voice/credentials",
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var ownerClient = configuredFactory.CreateCookieClient();
        using var loginResponse = await ownerClient.PostWithCsrfAsync(
            "/api/auth/login",
            new { email = ownerEmail, password = Password, rememberMe = false },
            cancellationToken);
        loginResponse.EnsureSuccessStatusCode();

        using var createResponse = await ownerClient.PostWithCsrfAsync(
            "/api/developer/external-voice/credentials",
            new { projectId = ProjectId, name = "正式站後端" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("no-store", createResponse.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", createResponse.Headers.GetValues("X-Content-Type-Options").Single());
        var issued = await ReadIssuedAsync(createResponse, cancellationToken);
        Assert.StartsWith("svd1.cred_", issued.AccessToken, StringComparison.Ordinal);
        Assert.EndsWith($"/{issued.CredentialId:D}", createResponse.Headers.Location?.OriginalString);

        await using (var scope = configuredFactory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var stored = await dbContext.ExternalVoiceCredentials.SingleAsync(
                credential => credential.Id == issued.CredentialId,
                cancellationToken);
            Assert.Equal(ownerId, stored.OwnerId);
            Assert.Equal(ConsumerKeyId, stored.ConsumerKeyId);
            Assert.Equal(64, stored.TokenSha256.Length);
            Assert.DoesNotContain(issued.AccessToken, stored.TokenSha256, StringComparison.Ordinal);
            Assert.Equal(stored.TokenSha256.ToLowerInvariant(), stored.TokenSha256);
            Assert.Equal(
                ExternalVoiceCredentialAuditActions.Created,
                (await dbContext.ExternalVoiceCredentialAudits.SingleAsync(
                    audit => audit.CredentialId == issued.CredentialId,
                    cancellationToken)).Action);
        }

        using var listResponse = await ownerClient.GetAsync(
            "/api/developer/external-voice/credentials",
            cancellationToken);
        listResponse.EnsureSuccessStatusCode();
        var listPayload = await listResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains(issued.KeyId, listPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.AccessToken, listPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenSha256", listPayload, StringComparison.OrdinalIgnoreCase);

        using var otherOwnerClient = await configuredFactory.CreateAuthenticatedClientAsync(cancellationToken);
        using var otherListResponse = await otherOwnerClient.GetAsync(
            "/api/developer/external-voice/credentials",
            cancellationToken);
        var otherListPayload = await otherListResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.DoesNotContain(issued.KeyId, otherListPayload, StringComparison.Ordinal);
        using var forbiddenRotateResponse = await otherOwnerClient.PostWithCsrfAsync(
            $"/api/developer/external-voice/credentials/{issued.CredentialId:D}/rotate",
            new { overlapMinutes = 0 },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, forbiddenRotateResponse.StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            await ProbeAuthenticationAsync(configuredFactory, issued.AccessToken, cancellationToken));
        await using (var scope = configuredFactory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            Assert.NotNull((await dbContext.ExternalVoiceCredentials.SingleAsync(
                credential => credential.Id == issued.CredentialId,
                cancellationToken)).LastUsedAtUtc);
        }

        using var rotateResponse = await ownerClient.PostWithCsrfAsync(
            $"/api/developer/external-voice/credentials/{issued.CredentialId:D}/rotate",
            new { overlapMinutes = 0 },
            cancellationToken);
        rotateResponse.EnsureSuccessStatusCode();
        var rotated = await ReadIssuedAsync(rotateResponse, cancellationToken);
        Assert.NotEqual(issued.AccessToken, rotated.AccessToken);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await ProbeAuthenticationAsync(configuredFactory, issued.AccessToken, cancellationToken));
        Assert.Equal(
            HttpStatusCode.BadRequest,
            await ProbeAuthenticationAsync(configuredFactory, rotated.AccessToken, cancellationToken));

        using var revokeResponse = await ownerClient.PostWithCsrfAsync(
            $"/api/developer/external-voice/credentials/{rotated.CredentialId:D}/revoke",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await ProbeAuthenticationAsync(configuredFactory, rotated.AccessToken, cancellationToken));

        using var auditResponse = await ownerClient.GetAsync(
            "/api/developer/external-voice/credentials/audit",
            cancellationToken);
        auditResponse.EnsureSuccessStatusCode();
        var auditPayload = await auditResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("created", auditPayload, StringComparison.Ordinal);
        Assert.Contains("rotated", auditPayload, StringComparison.Ordinal);
        Assert.Contains("revoked", auditPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.AccessToken, auditPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(rotated.AccessToken, auditPayload, StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> CreateConfiguredFactory(
        Guid ownerId,
        DateTimeOffset now) =>
        factory.WithWebHostBuilder(builder =>
        {
            var localPrefix = $"LocalClonePreview:AllowedProfiles:{Guid.NewGuid():D}";
            builder.UseSetting("LocalClonePreview:Enabled", "false");
            builder.UseSetting("LocalClonePreview:InternalToken", new string('t', 32));
            builder.UseSetting(
                "LocalClonePreview:AssetRootPath",
                Path.Combine(factory.StorageRoot, "developer-credential-assets"));
            builder.UseSetting($"{localPrefix}:Label", "credential test voice");
            builder.UseSetting($"{localPrefix}:ReferenceAudioRelativePath", "voice/reference.wav");
            builder.UseSetting($"{localPrefix}:TranscriptRelativePath", "voice/transcript.txt");
            builder.UseSetting($"{localPrefix}:ExpectedReferenceAudioSha256", new string('c', 64));
            builder.UseSetting($"{localPrefix}:ExpectedTranscriptSha256", new string('d', 64));

            var consumerPrefix = $"ExternalVoiceApi:Consumers:{ConsumerKeyId}";
            var voicePrefix = $"{consumerPrefix}:AllowedVoices:{VoiceAlias}";
            builder.UseSetting("ExternalVoiceApi:Enabled", "true");
            builder.UseSetting(
                $"{consumerPrefix}:AccessTier",
                ExternalVoiceAccessTiers.PrivateDevelopment);
            builder.UseSetting($"{consumerPrefix}:DisplayName", "credential test project");
            builder.UseSetting($"{consumerPrefix}:ProjectId", ProjectId);
            builder.UseSetting($"{consumerPrefix}:OwnerId", ownerId.ToString("D"));
            builder.UseSetting($"{consumerPrefix}:TokenSha256", new string('a', 64));
            builder.UseSetting(
                $"{consumerPrefix}:EffectiveAtUtc",
                now.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture));
            builder.UseSetting(
                $"{consumerPrefix}:ExpiresAtUtc",
                now.AddDays(29).ToString("O", CultureInfo.InvariantCulture));
            builder.UseSetting(
                $"{voicePrefix}:AuthorizationEvidenceRelativePath",
                "evidence/credential-test-grant.json");
            builder.UseSetting(
                $"{voicePrefix}:AuthorizationEvidenceSha256",
                new string('b', 64));
        });

    private static async Task CreateUserAsync(
        WebApplicationFactory<Program> configuredFactory,
        Guid ownerId,
        string ownerEmail)
    {
        await using var scope = configuredFactory.Services.CreateAsyncScope();
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

    private static async Task<HttpStatusCode> ProbeAuthenticationAsync(
        WebApplicationFactory<Program> configuredFactory,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var client = configuredFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/external/v1/speech")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Idempotency-Key", $"credential-test-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    private static async Task<(Guid CredentialId, string KeyId, string AccessToken)> ReadIssuedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        var credential = body.RootElement.GetProperty("credential");
        return (
            credential.GetProperty("id").GetGuid(),
            credential.GetProperty("keyId").GetString()
                ?? throw new InvalidOperationException("Credential response omitted keyId."),
            body.RootElement.GetProperty("accessToken").GetString()
                ?? throw new InvalidOperationException("Credential response omitted accessToken."));
    }
}

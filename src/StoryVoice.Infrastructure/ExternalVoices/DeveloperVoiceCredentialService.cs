using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Domain.ExternalVoices;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Infrastructure.ExternalVoices;

public sealed class DeveloperVoiceCredentialService(
    StoryVoiceDbContext dbContext,
    IOptions<ExternalVoiceApiOptions> options,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : IDeveloperVoiceCredentialService
{
    private const int MaximumActiveCredentialsPerProject = 5;
    private const int MaximumOverlapMinutes = 24 * 60;
    private const int MaximumAuditEntries = 100;
    private const string OneTimeNotice =
        "完整金鑰只顯示這一次；請立即保存到伺服器端 secret store。";

    public async Task<DeveloperVoiceCredentialList> ListAsync(
        CancellationToken cancellationToken)
    {
        var ownerId = currentUser.UserId;
        var value = options.Value;
        var now = timeProvider.GetUtcNow();
        var configured = value.Consumers
            .Where(pair => pair.Value.OwnerId == ownerId)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => CreateConfiguredSummary(pair.Key, pair.Value, now));
        var managedCredentials = await dbContext.ExternalVoiceCredentials
            .AsNoTracking()
            .Where(credential => credential.OwnerId == ownerId)
            .OrderByDescending(credential => credential.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var managed = managedCredentials.Select(credential =>
        {
            value.Consumers.TryGetValue(credential.ConsumerKeyId, out var consumer);
            return CreateManagedSummary(credential, consumer, now);
        });

        return new DeveloperVoiceCredentialList(configured.Concat(managed).ToArray());
    }

    public async Task<IssuedDeveloperVoiceCredential?> CreateAsync(
        CreateDeveloperVoiceCredentialRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = NormalizeName(request.Name);
        var project = FindOwnedProject(request.ProjectId);
        if (project is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        EnsureProjectCanIssue(project.Value.Consumer, now);
        await EnsureCredentialCapacityAsync(
            project.Value.KeyId,
            exceptCredentialId: null,
            now,
            cancellationToken);

        var issued = await CreateCredentialAsync(
            project.Value.KeyId,
            project.Value.Consumer,
            name,
            now,
            cancellationToken);
        dbContext.ExternalVoiceCredentials.Add(issued.Credential);
        dbContext.ExternalVoiceCredentialAudits.Add(ExternalVoiceCredentialAudit.Create(
            issued.Credential,
            ExternalVoiceCredentialAuditActions.Created,
            now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new IssuedDeveloperVoiceCredential(
            CreateManagedSummary(issued.Credential, project.Value.Consumer, now),
            issued.AccessToken,
            OneTimeNotice);
    }

    public async Task<IssuedDeveloperVoiceCredential?> RotateAsync(
        Guid credentialId,
        RotateDeveloperVoiceCredentialRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OverlapMinutes is < 0 or > MaximumOverlapMinutes)
        {
            throw new ArgumentException(
                $"Overlap minutes must be between 0 and {MaximumOverlapMinutes}.",
                nameof(request));
        }

        var ownerId = currentUser.UserId;
        var credential = await dbContext.ExternalVoiceCredentials.SingleOrDefaultAsync(
            candidate => candidate.Id == credentialId && candidate.OwnerId == ownerId,
            cancellationToken);
        if (credential is null)
        {
            return null;
        }

        var value = options.Value;
        if (!value.Consumers.TryGetValue(credential.ConsumerKeyId, out var consumer)
            || consumer.OwnerId != ownerId)
        {
            throw new InvalidOperationException("這個金鑰所屬專案已不可用，無法換發。");
        }

        var now = timeProvider.GetUtcNow();
        EnsureProjectCanIssue(consumer, now);
        if (credential.ExpiresAtUtc <= now
            || credential.RevokedAtUtc is not null)
        {
            throw new InvalidOperationException("只有仍有效且尚未排程撤銷的金鑰可以換發。");
        }

        await EnsureCredentialCapacityAsync(
            credential.ConsumerKeyId,
            credential.Id,
            now,
            cancellationToken);
        var issued = await CreateCredentialAsync(
            credential.ConsumerKeyId,
            consumer,
            credential.Name,
            now,
            cancellationToken);
        var revokeAt = now.AddMinutes(request.OverlapMinutes);
        credential.Revoke(revokeAt, issued.Credential.Id);
        dbContext.ExternalVoiceCredentials.Add(issued.Credential);
        dbContext.ExternalVoiceCredentialAudits.AddRange(
            ExternalVoiceCredentialAudit.Create(
                credential,
                ExternalVoiceCredentialAuditActions.Rotated,
                now,
                issued.Credential),
            ExternalVoiceCredentialAudit.Create(
                issued.Credential,
                ExternalVoiceCredentialAuditActions.Created,
                now,
                credential));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new IssuedDeveloperVoiceCredential(
            CreateManagedSummary(issued.Credential, consumer, now),
            issued.AccessToken,
            OneTimeNotice);
    }

    public async Task<bool> RevokeAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var ownerId = currentUser.UserId;
        var credential = await dbContext.ExternalVoiceCredentials.SingleOrDefaultAsync(
            candidate => candidate.Id == credentialId && candidate.OwnerId == ownerId,
            cancellationToken);
        if (credential is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (credential.Revoke(now))
        {
            dbContext.ExternalVoiceCredentialAudits.Add(ExternalVoiceCredentialAudit.Create(
                credential,
                ExternalVoiceCredentialAuditActions.Revoked,
                now));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<IReadOnlyList<DeveloperVoiceCredentialAuditSummary>> ListAuditAsync(
        CancellationToken cancellationToken)
    {
        var ownerId = currentUser.UserId;
        return await dbContext.ExternalVoiceCredentialAudits
            .AsNoTracking()
            .Where(audit => audit.OwnerId == ownerId)
            .OrderByDescending(audit => audit.OccurredAtUtc)
            .Take(MaximumAuditEntries)
            .Select(audit => new DeveloperVoiceCredentialAuditSummary(
                audit.Id,
                audit.CredentialKeyId,
                audit.Action,
                audit.OccurredAtUtc,
                audit.RelatedCredentialKeyId))
            .ToListAsync(cancellationToken);
    }

    private (string KeyId, ExternalVoiceConsumerOptions Consumer)? FindOwnedProject(
        string projectReference)
    {
        if (string.IsNullOrWhiteSpace(projectReference))
        {
            throw new ArgumentException("Project id is required.", nameof(projectReference));
        }

        var ownerId = currentUser.UserId;
        var normalized = projectReference.Trim();
        return options.Value.Consumers
            .Where(pair => pair.Value.OwnerId == ownerId
                && (string.Equals(pair.Key, normalized, StringComparison.Ordinal)
                    || string.Equals(pair.Value.ProjectId, normalized, StringComparison.Ordinal)))
            .OrderByDescending(pair => string.Equals(pair.Key, normalized, StringComparison.Ordinal))
            .Select(pair => ((string KeyId, ExternalVoiceConsumerOptions Consumer)?)(
                pair.Key,
                pair.Value))
            .FirstOrDefault();
    }

    private async Task EnsureCredentialCapacityAsync(
        string consumerKeyId,
        Guid? exceptCredentialId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ownerId = currentUser.UserId;
        var activeCount = await dbContext.ExternalVoiceCredentials.CountAsync(
            credential => credential.OwnerId == ownerId
                && credential.ConsumerKeyId == consumerKeyId
                && credential.Id != exceptCredentialId
                && credential.ExpiresAtUtc > now
                && (credential.RevokedAtUtc == null || credential.RevokedAtUtc > now),
            cancellationToken);
        if (activeCount >= MaximumActiveCredentialsPerProject)
        {
            throw new InvalidOperationException(
                $"每個專案最多只能有 {MaximumActiveCredentialsPerProject} 組有效金鑰。");
        }
    }

    private async Task<(ExternalVoiceCredential Credential, string AccessToken)> CreateCredentialAsync(
        string consumerKeyId,
        ExternalVoiceConsumerOptions consumer,
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string keyId;
        do
        {
            keyId = "cred_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        }
        while (options.Value.Consumers.ContainsKey(keyId)
            || await dbContext.ExternalVoiceCredentials.AnyAsync(
                credential => credential.KeyId == keyId,
                cancellationToken));

        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var accessToken = ResolveTokenPrefix(consumer.AccessTier) + keyId + "." + secret;
        var credential = ExternalVoiceCredential.Create(
            currentUser.UserId,
            consumerKeyId,
            keyId,
            name,
            Hash(accessToken),
            now,
            consumer.ExpiresAtUtc);
        return (credential, accessToken);
    }

    private static DeveloperVoiceCredentialSummary CreateConfiguredSummary(
        string keyId,
        ExternalVoiceConsumerOptions consumer,
        DateTimeOffset now) =>
        new(
            null,
            keyId,
            string.IsNullOrWhiteSpace(consumer.DisplayName)
                ? "設定檔金鑰"
                : consumer.DisplayName + "（設定檔）",
            consumer.ProjectId,
            consumer.AccessTier,
            ResolveTokenPrefix(consumer.AccessTier),
            false,
            null,
            null,
            consumer.ExpiresAtUtc,
            null,
            ResolveStatus(consumer, expiresAtUtc: consumer.ExpiresAtUtc, revokedAtUtc: null, now));

    private static DeveloperVoiceCredentialSummary CreateManagedSummary(
        ExternalVoiceCredential credential,
        ExternalVoiceConsumerOptions? consumer,
        DateTimeOffset now)
    {
        if (consumer is null || consumer.OwnerId != credential.OwnerId)
        {
            return new DeveloperVoiceCredentialSummary(
                credential.Id,
                credential.KeyId,
                credential.Name,
                credential.ConsumerKeyId,
                string.Empty,
                string.Empty,
                true,
                credential.CreatedAtUtc,
                credential.LastUsedAtUtc,
                credential.ExpiresAtUtc,
                credential.RevokedAtUtc,
                DeveloperVoiceCredentialStatuses.Revoked);
        }

        return new DeveloperVoiceCredentialSummary(
            credential.Id,
            credential.KeyId,
            credential.Name,
            consumer.ProjectId,
            consumer.AccessTier,
            ResolveTokenPrefix(consumer.AccessTier),
            true,
            credential.CreatedAtUtc,
            credential.LastUsedAtUtc,
            credential.ExpiresAtUtc,
            credential.RevokedAtUtc,
            ResolveStatus(consumer, credential.ExpiresAtUtc, credential.RevokedAtUtc, now));
    }

    private static string ResolveStatus(
        ExternalVoiceConsumerOptions consumer,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset? revokedAtUtc,
        DateTimeOffset now)
    {
        if (revokedAtUtc is { } revoked && revoked <= now)
        {
            return DeveloperVoiceCredentialStatuses.Revoked;
        }

        if (consumer.ExpiresAtUtc <= now || expiresAtUtc <= now)
        {
            return DeveloperVoiceCredentialStatuses.Expired;
        }

        if (consumer.EffectiveAtUtc > now)
        {
            return DeveloperVoiceCredentialStatuses.NotYetEffective;
        }

        return revokedAtUtc is not null
            ? DeveloperVoiceCredentialStatuses.RevocationScheduled
            : DeveloperVoiceCredentialStatuses.Active;
    }

    private void EnsureProjectCanIssue(
        ExternalVoiceConsumerOptions consumer,
        DateTimeOffset now)
    {
        if (!options.Value.Enabled)
        {
            throw new InvalidOperationException("合成聲線 API 目前未啟用，不能建立金鑰。");
        }

        if (consumer.ExpiresAtUtc <= now)
        {
            throw new InvalidOperationException("這個 API 專案已到期，不能建立金鑰。");
        }

        if (!ExternalVoiceAccessTiers.IsSupported(consumer.AccessTier))
        {
            throw new InvalidOperationException("這個 API 專案的存取層級不支援金鑰管理。");
        }
    }

    private static string NormalizeName(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized is not { Length: >= 2 and <= 80 }
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("金鑰名稱必須是 2 到 80 個可見字元。", nameof(name));
        }

        return normalized;
    }

    private static string ResolveTokenPrefix(string accessTier) => accessTier switch
    {
        ExternalVoiceAccessTiers.PrivateDevelopment =>
            ExternalVoiceAccessTiers.PrivateDevelopmentTokenPrefix,
        ExternalVoiceAccessTiers.SubscriptionCommercial =>
            ExternalVoiceAccessTiers.SubscriptionCommercialTokenPrefix,
        _ => string.Empty,
    };

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
}

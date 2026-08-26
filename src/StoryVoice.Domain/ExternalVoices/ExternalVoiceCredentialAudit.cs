namespace StoryVoice.Domain.ExternalVoices;

public static class ExternalVoiceCredentialAuditActions
{
    public const string Created = "created";
    public const string Rotated = "rotated";
    public const string Revoked = "revoked";
}

public sealed class ExternalVoiceCredentialAudit
{
    private ExternalVoiceCredentialAudit()
    {
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid CredentialId { get; private set; }

    public string CredentialKeyId { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public Guid? RelatedCredentialId { get; private set; }

    public string? RelatedCredentialKeyId { get; private set; }

    public static ExternalVoiceCredentialAudit Create(
        ExternalVoiceCredential credential,
        string action,
        DateTimeOffset occurredAtUtc,
        ExternalVoiceCredential? relatedCredential = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return new ExternalVoiceCredentialAudit
        {
            Id = Guid.NewGuid(),
            OwnerId = credential.OwnerId,
            CredentialId = credential.Id,
            CredentialKeyId = credential.KeyId,
            Action = action,
            OccurredAtUtc = occurredAtUtc,
            RelatedCredentialId = relatedCredential?.Id,
            RelatedCredentialKeyId = relatedCredential?.KeyId,
        };
    }
}

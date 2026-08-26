namespace StoryVoice.Domain.ExternalVoices;

public sealed class ExternalVoiceCredential
{
    private ExternalVoiceCredential()
    {
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    public string ConsumerKeyId { get; private set; } = string.Empty;

    public string KeyId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string TokenSha256 { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? LastUsedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? ReplacedByCredentialId { get; private set; }

    public static ExternalVoiceCredential Create(
        Guid ownerId,
        string consumerKeyId,
        string keyId,
        string name,
        string tokenSha256,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("Credential owner is required.", nameof(ownerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(consumerKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenSha256);
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentException("Credential expiry must be in the future.", nameof(expiresAtUtc));
        }

        return new ExternalVoiceCredential
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConsumerKeyId = consumerKeyId,
            KeyId = keyId,
            Name = name,
            TokenSha256 = tokenSha256,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
        };
    }

    public void RecordUse(DateTimeOffset usedAtUtc)
    {
        if (LastUsedAtUtc is null || usedAtUtc > LastUsedAtUtc)
        {
            LastUsedAtUtc = usedAtUtc;
        }
    }

    public bool Revoke(DateTimeOffset revokedAtUtc, Guid? replacedByCredentialId = null)
    {
        if (RevokedAtUtc is not null && RevokedAtUtc <= revokedAtUtc)
        {
            return false;
        }

        RevokedAtUtc = revokedAtUtc;
        ReplacedByCredentialId = replacedByCredentialId;
        return true;
    }
}

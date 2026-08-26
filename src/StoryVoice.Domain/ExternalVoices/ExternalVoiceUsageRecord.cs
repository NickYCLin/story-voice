namespace StoryVoice.Domain.ExternalVoices;

public sealed class ExternalVoiceUsageRecord
{
    private ExternalVoiceUsageRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    public string ConsumerKeyId { get; private set; } = string.Empty;

    public string CredentialKeyId { get; private set; } = string.Empty;

    public string ProjectId { get; private set; } = string.Empty;

    public string AccessTier { get; private set; } = string.Empty;

    public string RequestId { get; private set; } = string.Empty;

    public string? VoiceAlias { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public int StatusCode { get; private set; }

    public string Outcome { get; private set; } = string.Empty;

    public int DurationMilliseconds { get; private set; }

    public int? TextCharacters { get; private set; }

    public long ResponseBytes { get; private set; }

    public long AudioDurationMilliseconds { get; private set; }

    public static ExternalVoiceUsageRecord Create(
        Guid ownerId,
        string consumerKeyId,
        string credentialKeyId,
        string projectId,
        string accessTier,
        string requestId,
        string? voiceAlias,
        DateTimeOffset occurredAtUtc,
        int statusCode,
        string outcome,
        int durationMilliseconds,
        int? textCharacters,
        long responseBytes,
        long audioDurationMilliseconds)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("Usage owner is required.", nameof(ownerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(consumerKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessTier);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        if (occurredAtUtc == default || occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Usage timestamp must be UTC.", nameof(occurredAtUtc));
        }

        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        if (durationMilliseconds < 0
            || textCharacters is < 0
            || responseBytes < 0
            || audioDurationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        return new ExternalVoiceUsageRecord
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            ConsumerKeyId = consumerKeyId,
            CredentialKeyId = credentialKeyId,
            ProjectId = projectId,
            AccessTier = accessTier,
            RequestId = requestId,
            VoiceAlias = voiceAlias,
            OccurredAtUtc = occurredAtUtc,
            StatusCode = statusCode,
            Outcome = outcome,
            DurationMilliseconds = durationMilliseconds,
            TextCharacters = textCharacters,
            ResponseBytes = responseBytes,
            AudioDurationMilliseconds = audioDurationMilliseconds,
        };
    }
}

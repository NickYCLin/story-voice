namespace StoryVoice.Application.ExternalVoices;

public sealed record ExternalVoiceSynthesisRequest(
    string? Voice,
    string? Text);

public sealed record ExternalVoiceAudio(
    byte[] Content,
    string ContentType,
    long DurationMilliseconds = 0);

public interface IExternalVoiceSynthesisService
{
    Task<ExternalVoiceAudio> SynthesizeAsync(
        string consumerKeyId,
        ExternalVoiceSynthesisRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public enum ExternalVoiceSynthesisFailureKind
{
    InvalidRequest = 0,
    VoiceNotAvailable = 1,
    IdempotencyConflict = 2,
    RateLimited = 3,
    SynthesisUnavailable = 4,
    Disabled = 5,
}

public sealed class ExternalVoiceSynthesisException : Exception
{
    public ExternalVoiceSynthesisException(
        ExternalVoiceSynthesisFailureKind failureKind,
        int? retryAfterSeconds = null)
        : this(failureKind, retryAfterSeconds, null)
    {
    }

    public ExternalVoiceSynthesisException(
        ExternalVoiceSynthesisFailureKind failureKind,
        int? retryAfterSeconds,
        Exception? innerException)
        : base(MessageFor(failureKind), innerException)
    {
        FailureKind = failureKind;
        RetryAfterSeconds = retryAfterSeconds is > 0 ? retryAfterSeconds : null;
    }

    public ExternalVoiceSynthesisFailureKind FailureKind { get; }

    public int? RetryAfterSeconds { get; }

    public string StableCode => FailureKind switch
    {
        ExternalVoiceSynthesisFailureKind.InvalidRequest => "invalid_request",
        ExternalVoiceSynthesisFailureKind.VoiceNotAvailable => "voice_not_available",
        ExternalVoiceSynthesisFailureKind.IdempotencyConflict => "idempotency_conflict",
        ExternalVoiceSynthesisFailureKind.RateLimited => "rate_limited",
        ExternalVoiceSynthesisFailureKind.SynthesisUnavailable => "synthesis_unavailable",
        ExternalVoiceSynthesisFailureKind.Disabled => "synthesis_unavailable",
        _ => "synthesis_unavailable",
    };

    private static string MessageFor(ExternalVoiceSynthesisFailureKind failureKind) => failureKind switch
    {
        ExternalVoiceSynthesisFailureKind.InvalidRequest => "The external voice request is invalid.",
        ExternalVoiceSynthesisFailureKind.VoiceNotAvailable => "The requested voice is not available.",
        ExternalVoiceSynthesisFailureKind.IdempotencyConflict => "The idempotency key was already used for a different request.",
        ExternalVoiceSynthesisFailureKind.RateLimited => "The external voice request limit was reached.",
        ExternalVoiceSynthesisFailureKind.Disabled => "External voice synthesis is disabled.",
        _ => "External voice synthesis is temporarily unavailable.",
    };
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.ExternalVoices;

namespace StoryVoice.Infrastructure.ExternalVoices;

internal sealed class DeveloperVoicePlaygroundService(
    IOptions<ExternalVoiceApiOptions> options,
    ICurrentUser currentUser,
    IExternalVoiceSynthesisService synthesisService,
    IExternalVoiceRequestRateLimiter rateLimiter,
    IExternalVoiceUsageRecorder usageRecorder,
    TimeProvider timeProvider,
    ILogger<DeveloperVoicePlaygroundService> logger) : IDeveloperVoicePlaygroundService
{
    private const string PlaygroundCredentialKeyId = "owner-session-playground";
    private const int UnavailableRetryAfterSeconds = 30;
    private const int OkStatusCode = 200;
    private const int BadRequestStatusCode = 400;
    private const int NotFoundStatusCode = 404;
    private const int ConflictStatusCode = 409;
    private const int TooManyRequestsStatusCode = 429;
    private const int ClientClosedRequestStatusCode = 499;
    private const int ServiceUnavailableStatusCode = 503;

    public async Task<DeveloperVoicePlaygroundResult> SynthesizeAsync(
        DeveloperVoicePlaygroundRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = CreateRequestId();
        var occurredAtUtc = timeProvider.GetUtcNow();
        var started = Stopwatch.GetTimestamp();
        var projectReference = NormalizeProjectReference(request.ProjectId);
        if (projectReference is null)
        {
            return FailureWithoutUsage(
                requestId,
                started,
                BadRequestStatusCode,
                "invalid_request");
        }

        var ownerId = currentUser.UserId;
        var project = FindOwnedProject(projectReference, ownerId);
        if (project is null)
        {
            return FailureWithoutUsage(
                requestId,
                started,
                NotFoundStatusCode,
                "voice_not_available");
        }

        var (consumerKeyId, consumer) = project.Value;
        var voiceAlias = NormalizeVoiceAlias(request.Voice);
        var textCharacters = CountTextCharacters(request.Text);
        var statusCode = ServiceUnavailableStatusCode;
        var outcome = "synthesis_unavailable";
        byte[]? audioContent = null;
        string? contentType = null;
        long audioDurationMilliseconds = 0;
        int? retryAfterSeconds = UnavailableRetryAfterSeconds;
        var durationMilliseconds = 0;

        try
        {
            if (!rateLimiter.TryAcquire(consumerKeyId, out var rateLimitRetryAfterSeconds))
            {
                throw new ExternalVoiceSynthesisException(
                    ExternalVoiceSynthesisFailureKind.RateLimited,
                    rateLimitRetryAfterSeconds);
            }

            if (!options.Value.Enabled)
            {
                throw new ExternalVoiceSynthesisException(ExternalVoiceSynthesisFailureKind.Disabled);
            }

            var audio = await synthesisService.SynthesizeAsync(
                consumerKeyId,
                new ExternalVoiceSynthesisRequest(request.Voice, request.Text),
                request.IdempotencyKey,
                cancellationToken);
            if (audio.Content.Length == 0
                || audio.Content.Length > options.Value.MaximumResponseBytes
                || !string.Equals(audio.ContentType, "audio/wav", StringComparison.Ordinal))
            {
                throw new ExternalVoiceSynthesisException(
                    ExternalVoiceSynthesisFailureKind.SynthesisUnavailable);
            }

            statusCode = OkStatusCode;
            outcome = ExternalVoiceUsageOutcomes.Succeeded;
            audioContent = audio.Content.ToArray();
            contentType = audio.ContentType;
            audioDurationMilliseconds = Math.Max(0, audio.DurationMilliseconds);
            retryAfterSeconds = null;
        }
        catch (ExternalVoiceSynthesisException exception)
        {
            (statusCode, outcome, retryAfterSeconds) = MapFailure(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            statusCode = ClientClosedRequestStatusCode;
            outcome = ExternalVoiceUsageOutcomes.RequestCancelled;
            retryAfterSeconds = null;
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Developer playground synthesis failed for project {ProjectId} and request {RequestId}.",
                consumer.ProjectId,
                requestId);
        }
        finally
        {
            durationMilliseconds = ElapsedMilliseconds(started);
            try
            {
                usageRecorder.TryEnqueue(
                    new ExternalVoiceUsageWrite(
                        ownerId,
                        consumerKeyId,
                        PlaygroundCredentialKeyId,
                        string.IsNullOrWhiteSpace(consumer.ProjectId)
                            ? consumerKeyId
                            : consumer.ProjectId,
                        consumer.AccessTier,
                        requestId,
                        consumer.AllowedVoices.ContainsKey(voiceAlias ?? string.Empty)
                            ? voiceAlias
                            : null,
                        occurredAtUtc,
                        statusCode,
                        outcome,
                        durationMilliseconds,
                        textCharacters,
                        audioContent?.LongLength ?? 0,
                        audioDurationMilliseconds));
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to enqueue developer playground usage record {RequestId}.",
                    requestId);
            }
        }

        return new DeveloperVoicePlaygroundResult(
            requestId,
            statusCode,
            outcome,
            durationMilliseconds,
            audioContent,
            contentType,
            audioDurationMilliseconds,
            retryAfterSeconds);
    }

    private (string KeyId, ExternalVoiceConsumerOptions Consumer)? FindOwnedProject(
        string projectReference,
        Guid ownerId) =>
        options.Value.Consumers
            .Where(pair => pair.Value.OwnerId == ownerId
                && (string.Equals(pair.Key, projectReference, StringComparison.Ordinal)
                    || string.Equals(pair.Value.ProjectId, projectReference, StringComparison.Ordinal)))
            .OrderByDescending(pair => string.Equals(pair.Key, projectReference, StringComparison.Ordinal))
            .Select(pair => ((string KeyId, ExternalVoiceConsumerOptions Consumer)?)(pair.Key, pair.Value))
            .FirstOrDefault();

    private static (int StatusCode, string Outcome, int? RetryAfterSeconds) MapFailure(
        ExternalVoiceSynthesisException exception) => exception.FailureKind switch
        {
            ExternalVoiceSynthesisFailureKind.InvalidRequest =>
                (BadRequestStatusCode, "invalid_request", null),
            ExternalVoiceSynthesisFailureKind.VoiceNotAvailable =>
                (NotFoundStatusCode, "voice_not_available", null),
            ExternalVoiceSynthesisFailureKind.IdempotencyConflict =>
                (ConflictStatusCode, "idempotency_conflict", null),
            ExternalVoiceSynthesisFailureKind.RateLimited =>
                (TooManyRequestsStatusCode, "rate_limited", Math.Max(1, exception.RetryAfterSeconds ?? 60)),
            _ =>
                (ServiceUnavailableStatusCode, "synthesis_unavailable", UnavailableRetryAfterSeconds),
        };

    private static DeveloperVoicePlaygroundResult FailureWithoutUsage(
        string requestId,
        long started,
        int statusCode,
        string outcome) =>
        new(
            requestId,
            statusCode,
            outcome,
            ElapsedMilliseconds(started),
            null,
            null,
            0,
            null);

    private static string? NormalizeProjectReference(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length is >= 1 and <= 128
            && normalized.All(character => character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or ':')
            ? normalized
            : null;
    }

    private static string? NormalizeVoiceAlias(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
            return ExternalVoiceApiOptionsValidator.IsCanonicalVoiceAlias(normalized)
                ? normalized
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int? CountTextCharacters(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return value.Normalize(NormalizationForm.FormKC).Trim().EnumerateRunes().Count();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int ElapsedMilliseconds(long started) =>
        (int)Math.Clamp(
            Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds),
            0,
            int.MaxValue);

    private static string CreateRequestId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

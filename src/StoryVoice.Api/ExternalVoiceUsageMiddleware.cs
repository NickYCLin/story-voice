using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;

namespace StoryVoice.Api;

internal sealed class ExternalVoiceUsageMiddleware(
    RequestDelegate next,
    ILogger<ExternalVoiceUsageMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        IOptions<ExternalVoiceApiOptions> options,
        IExternalVoiceUsageRecorder recorder,
        TimeProvider timeProvider)
    {
        if (!HttpMethods.IsPost(httpContext.Request.Method)
            || !string.Equals(
                httpContext.Request.Path.Value,
                "/api/external/v1/speech",
                StringComparison.Ordinal))
        {
            await next(httpContext);
            return;
        }

        var consumerKeyId = httpContext.User.FindFirst(
            ExternalVoiceAuthenticationDefaults.ConsumerKeyIdClaim)?.Value;
        var credentialKeyId = httpContext.User.FindFirst(
            ExternalVoiceAuthenticationDefaults.CredentialKeyIdClaim)?.Value;
        var ownerIdText = httpContext.User.FindFirst(
            ExternalVoiceAuthenticationDefaults.ExternalOwnerIdClaim)?.Value;
        if (string.IsNullOrEmpty(consumerKeyId)
            || string.IsNullOrEmpty(credentialKeyId)
            || !Guid.TryParseExact(ownerIdText, "D", out var ownerId)
            || !options.Value.Consumers.TryGetValue(consumerKeyId, out var consumer)
            || consumer is null
            || consumer.OwnerId != ownerId)
        {
            await next(httpContext);
            return;
        }

        var requestId = CreateRequestId();
        var feature = new ExternalVoiceUsageFeature(consumer);
        httpContext.Features.Set(feature);
        httpContext.Response.Headers["X-StoryVoice-Request-Id"] = requestId;
        var occurredAtUtc = timeProvider.GetUtcNow();
        var started = Stopwatch.GetTimestamp();
        Exception? requestException = null;
        try
        {
            await next(httpContext);
        }
        catch (OperationCanceledException exception) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            requestException = exception;
            feature.MarkFailure(ExternalVoiceUsageOutcomes.RequestCancelled);
            throw;
        }
        catch (Exception exception)
        {
            requestException = exception;
            feature.MarkFailure("synthesis_unavailable");
            throw;
        }
        finally
        {
            var statusCode = feature.StatusCode
                ?? (requestException is OperationCanceledException
                    ? 499
                    : requestException is null
                        ? httpContext.Response.StatusCode
                        : StatusCodes.Status500InternalServerError);
            if (statusCode is < 100 or > 599)
            {
                statusCode = StatusCodes.Status500InternalServerError;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var durationMilliseconds = (int)Math.Clamp(
                Math.Ceiling(elapsed.TotalMilliseconds),
                0,
                int.MaxValue);
            var responseBytes = Math.Max(
                0,
                feature.ResponseBytes ?? httpContext.Response.ContentLength ?? 0);
            var outcome = feature.Outcome ?? OutcomeFromStatus(statusCode);
            try
            {
                recorder.TryEnqueue(
                    new ExternalVoiceUsageWrite(
                        ownerId,
                        consumerKeyId,
                        credentialKeyId,
                        string.IsNullOrWhiteSpace(consumer.ProjectId)
                            ? consumerKeyId
                            : consumer.ProjectId,
                        consumer.AccessTier,
                        requestId,
                        feature.VoiceAlias,
                        occurredAtUtc,
                        statusCode,
                        outcome,
                        durationMilliseconds,
                        feature.TextCharacters,
                        responseBytes,
                        feature.AudioDurationMilliseconds));
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to enqueue external voice usage record {RequestId}.",
                    requestId);
            }
        }
    }

    private static string CreateRequestId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string OutcomeFromStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status200OK => ExternalVoiceUsageOutcomes.Succeeded,
        StatusCodes.Status400BadRequest => "invalid_request",
        StatusCodes.Status404NotFound => "voice_not_available",
        StatusCodes.Status409Conflict => "idempotency_conflict",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        _ => "synthesis_unavailable",
    };
}

internal sealed class ExternalVoiceUsageFeature(ExternalVoiceConsumerOptions consumer)
{
    public string? VoiceAlias { get; private set; }

    public int? TextCharacters { get; private set; }

    public string? Outcome { get; private set; }

    public int? StatusCode { get; private set; }

    public long? ResponseBytes { get; private set; }

    public long AudioDurationMilliseconds { get; private set; }

    public void CaptureRequest(ExternalVoiceSynthesisRequest request)
    {
        if (TryNormalize(request.Voice, out var voice)
            && consumer.AllowedVoices.ContainsKey(voice))
        {
            VoiceAlias = voice;
        }

        if (request.Text is not null)
        {
            try
            {
                TextCharacters = request.Text.Normalize(NormalizationForm.FormKC)
                    .Trim()
                    .EnumerateRunes()
                    .Count();
            }
            catch (ArgumentException)
            {
                TextCharacters = null;
            }
        }
    }

    public void MarkSuccess(ExternalVoiceAudio audio)
    {
        Outcome = ExternalVoiceUsageOutcomes.Succeeded;
        StatusCode = StatusCodes.Status200OK;
        ResponseBytes = audio.Content.LongLength;
        AudioDurationMilliseconds = Math.Max(0, audio.DurationMilliseconds);
    }

    public void MarkFailure(string outcome, int? statusCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        Outcome = outcome;
        StatusCode = statusCode;
    }

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
        {
            return false;
        }

        try
        {
            normalized = value.Normalize(NormalizationForm.FormKC).Trim();
            return normalized.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

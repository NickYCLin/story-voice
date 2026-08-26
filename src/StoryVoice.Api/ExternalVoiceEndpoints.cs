using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;

namespace StoryVoice.Api;

public static class ExternalVoiceEndpoints
{
    public const string RateLimitPolicy = "StoryVoiceExternalVoiceFixedWindow";

    private const string JsonContentType = "application/json";
    private const string WaveContentType = "audio/wav";
    private const int UnavailableRetryAfterSeconds = 30;

    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static IEndpointRouteBuilder MapExternalVoiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/external/v1/speech", SynthesizeAsync)
            .WithTags("External voice")
            .WithName("SynthesizeExternalVoice")
            .WithSummary("Synthesize speech with an authorized StoryVoice character voice")
            .WithDescription(
                "Uses an explicitly granted character voice. This endpoint does not expose a voice catalog.")
            .Produces(StatusCodes.Status200OK, contentType: WaveContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(StoryVoicePolicies.ExternalVoiceSynthesis)
            .RequireRateLimiting(RateLimitPolicy);

        return endpoints;
    }

    public static async ValueTask WriteRateLimitRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(
            System.Threading.RateLimiting.MetadataName.RetryAfter,
            out var retryAfterValue)
            ? Math.Max(1, (int)Math.Ceiling(retryAfterValue.TotalSeconds))
            : 60;

        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        await WriteProblemAsync(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            "Rate limit exceeded",
            "The external voice request limit was reached.",
            "rate_limited",
            cancellationToken);
    }

    private static async Task<IResult> SynthesizeAsync(
        HttpContext httpContext,
        IExternalVoiceSynthesisService synthesisService,
        IOptions<ExternalVoiceApiOptions> options,
        ILogger<ExternalVoiceEndpointLog> logger,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                httpContext.Request.Path.Value,
                "/api/external/v1/speech",
                StringComparison.Ordinal)
            || httpContext.Request.QueryString.HasValue)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                "Use the canonical external voice API path.",
                "invalid_request");
        }

        if (!IsJsonRequest(httpContext.Request))
        {
            return Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported media type",
                "Content-Type must be application/json and Content-Encoding must be omitted.",
                "unsupported_media_type");
        }

        if (!TryGetIdempotencyKey(httpContext.Request, out var idempotencyKey))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                "A single valid Idempotency-Key header is required.",
                "invalid_request");
        }

        byte[] body;
        try
        {
            body = await ReadBodyAsync(
                httpContext.Request,
                options.Value.MaximumRequestBytes,
                cancellationToken);
        }
        catch (ExternalVoiceRequestTooLargeException)
        {
            return Problem(
                StatusCodes.Status413PayloadTooLarge,
                "Request too large",
                "The request body exceeds the configured limit.",
                "request_too_large");
        }

        ExternalVoiceSynthesisRequest? request;
        try
        {
            if (!HasStrictObjectShape(body))
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "Invalid request",
                    "The JSON request body is invalid.",
                    "invalid_request");
            }

            request = JsonSerializer.Deserialize<ExternalVoiceSynthesisRequest>(body, StrictJsonOptions);
        }
        catch (JsonException)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                "The JSON request body is invalid.",
                "invalid_request");
        }

        if (request is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                "The JSON request body must be an object.",
                "invalid_request");
        }

        httpContext.Features.Get<ExternalVoiceUsageFeature>()?.CaptureRequest(request);

        var consumerKeyId = httpContext.User.FindFirst(
            ExternalVoiceAuthenticationDefaults.ConsumerKeyIdClaim)?.Value;
        if (string.IsNullOrEmpty(consumerKeyId))
        {
            // The authorization policy normally makes this unreachable. Keep the endpoint
            // fail-closed if its metadata or middleware ordering is ever changed.
            return Problem(
                StatusCodes.Status401Unauthorized,
                "Invalid API key",
                "A valid external voice API key is required.",
                "invalid_api_key");
        }

        try
        {
            var audio = await synthesisService.SynthesizeAsync(
                consumerKeyId,
                request,
                idempotencyKey,
                cancellationToken);
            if (audio.Content.Length == 0
                || audio.Content.Length > options.Value.MaximumResponseBytes
                || !string.Equals(audio.ContentType, WaveContentType, StringComparison.Ordinal))
            {
                logger.LogError("External voice synthesis returned an invalid audio response.");
                return UnavailableProblem();
            }

            httpContext.Features.Get<ExternalVoiceUsageFeature>()?.MarkSuccess(audio);
            ApplyPrivateNoStoreHeaders(httpContext.Response);
            httpContext.Response.Headers.Pragma = "no-cache";
            httpContext.Response.Headers.XContentTypeOptions = "nosniff";
            httpContext.Response.ContentLength = audio.Content.LongLength;
            return Results.Bytes(audio.Content, WaveContentType);
        }
        catch (ExternalVoiceSynthesisException exception)
        {
            return MapSynthesisFailure(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "External voice synthesis failed unexpectedly.");
            return UnavailableProblem();
        }
    }

    private static IResult MapSynthesisFailure(ExternalVoiceSynthesisException exception) =>
        exception.FailureKind switch
        {
            ExternalVoiceSynthesisFailureKind.InvalidRequest => Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                "The external voice request is invalid.",
                "invalid_request"),
            ExternalVoiceSynthesisFailureKind.VoiceNotAvailable => Problem(
                StatusCodes.Status404NotFound,
                "Voice not available",
                "The requested voice is not available to this API consumer.",
                "voice_not_available"),
            ExternalVoiceSynthesisFailureKind.IdempotencyConflict => Problem(
                StatusCodes.Status409Conflict,
                "Idempotency conflict",
                "The idempotency key was already used for a different request.",
                "idempotency_conflict"),
            ExternalVoiceSynthesisFailureKind.RateLimited => RateLimitedProblem(exception.RetryAfterSeconds),
            _ => UnavailableProblem(),
        };

    // The public docs promise 503 responses carry Retry-After; keep the header on
    // every synthesis_unavailable path so documented client retry loops work.
    private static IResult UnavailableProblem() =>
        new ExternalVoiceProblemResult(
            StatusCodes.Status503ServiceUnavailable,
            "Synthesis unavailable",
            "External voice synthesis is unavailable.",
            "synthesis_unavailable",
            UnavailableRetryAfterSeconds);

    private static IResult RateLimitedProblem(int? retryAfterSeconds)
    {
        var retryAfter = Math.Max(1, retryAfterSeconds ?? 60);
        return new ExternalVoiceProblemResult(
            StatusCodes.Status429TooManyRequests,
            "Rate limit exceeded",
            "The external voice request limit was reached.",
            "rate_limited",
            retryAfter);
    }

    private static IResult Problem(int status, string title, string detail, string code) =>
        new ExternalVoiceProblemResult(status, title, detail, code, null);

    private static bool IsJsonRequest(HttpRequest request)
    {
        if (request.Headers.ContainsKey(HeaderNames.ContentEncoding)
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
        {
            return false;
        }

        return string.Equals(contentType.MediaType.Value, JsonContentType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetIdempotencyKey(HttpRequest request, out string idempotencyKey)
    {
        idempotencyKey = string.Empty;
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values)
            || values.Count != 1)
        {
            return false;
        }

        var candidate = values[0];
        if (candidate is not { Length: >= 16 and <= 64 }
            || candidate.Any(character => character is not (
                >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-')))
        {
            return false;
        }

        idempotencyKey = candidate;
        return true;
    }

    private static bool HasStrictObjectShape(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        var seenProperties = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return !reader.Read();
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            var propertyName = reader.GetString();
            if (propertyName is not ("voice" or "text")
                || !seenProperties.Add(propertyName))
            {
                return false;
            }

            if (!reader.Read())
            {
                return false;
            }

            reader.Skip();
        }

        return false;
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpRequest request,
        int maximumRequestBytes,
        CancellationToken cancellationToken)
    {
        if (maximumRequestBytes <= 0
            || request.ContentLength is > 0 && request.ContentLength > maximumRequestBytes)
        {
            throw new ExternalVoiceRequestTooLargeException();
        }

        var initialCapacity = request.ContentLength is > 0
            ? (int)Math.Min(request.ContentLength.Value, maximumRequestBytes)
            : Math.Min(maximumRequestBytes, 8 * 1024);
        using var output = new MemoryStream(initialCapacity);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximumRequestBytes + 1, 8 * 1024));
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumRequestBytes)
                {
                    throw new ExternalVoiceRequestTooLargeException();
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static void ApplyPrivateNoStoreHeaders(HttpResponse response) =>
        response.Headers.CacheControl = "private, no-store";

    internal static async ValueTask WriteProblemAsync(
        HttpContext httpContext,
        int status,
        string title,
        string detail,
        string code,
        CancellationToken cancellationToken)
    {
        var result = new ExternalVoiceProblemResult(status, title, detail, code, null);
        await result.ExecuteAsync(httpContext);
    }

    private sealed class ExternalVoiceRequestTooLargeException : Exception;

    internal sealed class ExternalVoiceEndpointLog;

    private sealed class ExternalVoiceProblemResult(
        int status,
        string title,
        string detail,
        string code,
        int? retryAfterSeconds) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Features.Get<ExternalVoiceUsageFeature>()?.MarkFailure(code, status);
            ApplyPrivateNoStoreHeaders(httpContext.Response);
            httpContext.Response.Headers.Pragma = "no-cache";
            httpContext.Response.Headers.XContentTypeOptions = "nosniff";
            if (retryAfterSeconds is not null)
            {
                httpContext.Response.Headers.RetryAfter =
                    retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
            }

            var problemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
            };
            problemDetails.Extensions["code"] = code;
            httpContext.Response.ContentType = "application/problem+json";
            await JsonSerializer.SerializeAsync(
                httpContext.Response.Body,
                problemDetails,
                cancellationToken: httpContext.RequestAborted);
        }
    }
}

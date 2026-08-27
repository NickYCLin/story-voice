using System.Globalization;
using StoryVoice.Application.ExternalVoices;

namespace StoryVoice.Api;

public static class DeveloperConsoleEndpoints
{
    public static IEndpointRouteBuilder MapDeveloperConsoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/developer")
            .WithTags("Developer console")
            .RequireAuthorization(StoryVoicePolicies.UserSession);

        group.MapGet("/external-voice/overview", async (
            HttpContext httpContext,
            IDeveloperVoiceConsoleService service,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var overview = await service.GetOverviewAsync(cancellationToken);
            return Results.Ok(overview);
        });

        group.MapGet("/external-voice/credentials", async (
            HttpContext httpContext,
            IDeveloperVoiceCredentialService service,
            CancellationToken cancellationToken) =>
        {
            ApplyNoStore(httpContext.Response);
            return Results.Ok(await service.ListAsync(cancellationToken));
        });

        group.MapGet("/external-voice/credentials/audit", async (
            HttpContext httpContext,
            IDeveloperVoiceCredentialService service,
            CancellationToken cancellationToken) =>
        {
            ApplyNoStore(httpContext.Response);
            return Results.Ok(await service.ListAuditAsync(cancellationToken));
        });

        group.MapGet("/external-voice/usage", async (
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            string? projectId,
            string? voice,
            int? limit,
            HttpContext httpContext,
            IDeveloperVoiceUsageService service,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            ApplyNoStore(httpContext.Response);
            var now = timeProvider.GetUtcNow();
            var query = new DeveloperVoiceUsageQuery(
                (fromUtc ?? now.AddHours(-24)).ToUniversalTime(),
                (toUtc ?? now).ToUniversalTime(),
                projectId,
                voice,
                limit ?? 50);
            try
            {
                return Results.Ok(await service.GetUsageAsync(query, cancellationToken));
            }
            catch (ArgumentException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid usage query",
                    detail: "Use a UTC range up to 90 days and valid project, voice and limit filters.");
            }
        });

        group.MapPost("/external-voice/playground", async (
            DeveloperVoicePlaygroundRequest request,
            HttpContext httpContext,
            IDeveloperVoicePlaygroundService service,
            CancellationToken cancellationToken) =>
        {
            ApplyNoStore(httpContext.Response);
            var result = await service.SynthesizeAsync(request, cancellationToken);
            ApplyPlaygroundHeaders(httpContext.Response, result);
            if (result.StatusCode == StatusCodes.Status200OK
                && result.AudioContent is { Length: > 0 }
                && string.Equals(result.ContentType, "audio/wav", StringComparison.Ordinal))
            {
                httpContext.Response.ContentLength = result.AudioContent.LongLength;
                return Results.Bytes(result.AudioContent, result.ContentType);
            }

            var (title, detail) = PlaygroundProblem(result.StatusCode);
            return Results.Problem(
                statusCode: result.StatusCode,
                title: title,
                detail: detail,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = result.Outcome,
                    ["requestId"] = result.RequestId,
                });
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/external-voice/credentials", async (
            CreateDeveloperVoiceCredentialRequest request,
            HttpContext httpContext,
            IDeveloperVoiceCredentialService service,
            CancellationToken cancellationToken) =>
        {
            ApplyNoStore(httpContext.Response);
            var issued = await service.CreateAsync(request, cancellationToken);
            return issued is null
                ? Results.NotFound()
                : Results.Created(
                    $"{httpContext.Request.PathBase}/api/developer/external-voice/credentials/{issued.Credential.Id:D}",
                    issued);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/external-voice/credentials/{credentialId:guid}/rotate", async (
            Guid credentialId,
            RotateDeveloperVoiceCredentialRequest request,
            HttpContext httpContext,
            IDeveloperVoiceCredentialService service,
            CancellationToken cancellationToken) =>
        {
            ApplyNoStore(httpContext.Response);
            var issued = await service.RotateAsync(credentialId, request, cancellationToken);
            return issued is null ? Results.NotFound() : Results.Ok(issued);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/external-voice/credentials/{credentialId:guid}/revoke", async (
            Guid credentialId,
            HttpContext httpContext,
            IDeveloperVoiceCredentialService service,
            CancellationToken cancellationToken) =>
        {
            ApplyNoStore(httpContext.Response);
            return await service.RevokeAsync(credentialId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }

    private static void ApplyNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
    }

    private static void ApplyPlaygroundHeaders(
        HttpResponse response,
        DeveloperVoicePlaygroundResult result)
    {
        response.Headers["X-StoryVoice-Request-Id"] = result.RequestId;
        response.Headers["X-StoryVoice-Latency-Ms"] =
            result.DurationMilliseconds.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-StoryVoice-Audio-Duration-Ms"] =
            result.AudioDurationMilliseconds.ToString(CultureInfo.InvariantCulture);
        if (result.RetryAfterSeconds is { } retryAfterSeconds)
        {
            response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static (string Title, string Detail) PlaygroundProblem(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest =>
            ("Invalid request", "Check the project, voice, text and idempotency key."),
        StatusCodes.Status404NotFound =>
            ("Voice not available", "The project or voice is not available to the signed-in owner."),
        StatusCodes.Status409Conflict =>
            ("Idempotency conflict", "The idempotency key was already used for different input."),
        StatusCodes.Status429TooManyRequests =>
            ("Rate limit exceeded", "The playground request limit was reached."),
        _ =>
            ("Synthesis unavailable", "Voice synthesis is temporarily unavailable."),
    };
}

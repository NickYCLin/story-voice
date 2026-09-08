using StoryVoice.Application.Narrations;

namespace StoryVoice.Api;

public static class NarrationEndpoints
{
    public static IEndpointRouteBuilder MapNarrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var bookGroup = endpoints.MapGroup("/api/books/{bookId:guid}/narrations")
            .WithTags("Narrations")
            .RequireAuthorization(StoryVoicePolicies.UserSession);

        bookGroup.MapGet("/", async (
            Guid bookId,
            INarrationService service,
            CancellationToken cancellationToken) =>
        {
            var jobs = await service.ListAsync(bookId, cancellationToken);
            return jobs is null ? Results.NotFound() : Results.Ok(jobs);
        });

        bookGroup.MapPost("/", async (
            Guid bookId,
            CreateNarrationRequest request,
            HttpContext httpContext,
            INarrationService service,
            CancellationToken cancellationToken) =>
        {
            var job = await service.CreateAsync(bookId, request, cancellationToken);
            return job is null
                ? Results.NotFound()
                : Results.Created($"{httpContext.Request.PathBase}/api/narrations/{job.Id}", job);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        var jobGroup = endpoints.MapGroup("/api/narrations/{jobId:guid}")
            .WithTags("Narrations")
            .RequireAuthorization(StoryVoicePolicies.UserSession);

        jobGroup.MapGet("/", async (
            Guid jobId,
            INarrationService service,
            CancellationToken cancellationToken) =>
        {
            var job = await service.GetAsync(jobId, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        jobGroup.MapGet("/usage", async (
            Guid jobId, HttpContext httpContext, INarrationService service, CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "private, no-store";
            var usage = await service.GetUsageAsync(jobId, cancellationToken);
            return usage is null ? Results.NotFound() : Results.Ok(usage);
        });

        jobGroup.MapPost("/cancel", async (
            Guid jobId,
            INarrationService service,
            CancellationToken cancellationToken) =>
        {
            var job = await service.CancelAsync(jobId, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        jobGroup.MapGet("/timeline", async (
            Guid jobId,
            HttpContext httpContext,
            INarrationService service,
            CancellationToken cancellationToken) =>
        {
            var timeline = await service.GetTimelineAsync(jobId, cancellationToken);
            httpContext.Response.Headers.CacheControl = "private, no-store";
            return timeline is null ? Results.NotFound() : Results.Ok(timeline);
        });

        jobGroup.MapGet("/audio", async (
            Guid jobId,
            HttpContext httpContext,
            INarrationService service,
            CancellationToken cancellationToken) =>
        {
            var audio = await service.GetAudioAsync(jobId, cancellationToken);
            httpContext.Response.Headers.CacheControl = "private, no-store";
            httpContext.Response.Headers.XContentTypeOptions = "nosniff";
            return audio is null
                ? Results.NotFound()
                : Results.File(audio.AbsolutePath, audio.ContentType, enableRangeProcessing: true);
        });

        jobGroup.MapGet("/progress", async (
            Guid jobId, HttpContext httpContext, INarrationService service, CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "private, no-store";
            var progress = await service.GetListeningProgressAsync(jobId, cancellationToken);
            return progress is null ? Results.NotFound() : Results.Ok(progress);
        });

        jobGroup.MapPut("/progress", async (
            Guid jobId, SaveListeningProgressRequest request, HttpContext httpContext,
            INarrationService service, CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "private, no-store";
            var result = await service.SaveListeningProgressAsync(jobId, request, cancellationToken);
            if (result is null) return Results.NotFound();
            return result.Conflict ? Results.Conflict(result.Progress) : Results.Ok(result.Progress);
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }
}

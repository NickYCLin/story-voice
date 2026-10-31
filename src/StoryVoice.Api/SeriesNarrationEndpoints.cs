using StoryVoice.Application.Narrations;

namespace StoryVoice.Api;

public static class SeriesNarrationEndpoints
{
    public static IEndpointRouteBuilder MapSeriesNarrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/series/{seriesId:guid}/narration-rebuilds")
            .WithTags("SeriesNarration")
            .RequireAuthorization(StoryVoicePolicies.UserSession);

        group.MapGet("/", async (Guid seriesId, HttpContext httpContext, ISeriesNarrationService service, CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "private, no-store";
            var batches = await service.ListRebuildsAsync(seriesId, cancellationToken);
            return batches is null ? Results.NotFound() : Results.Ok(batches);
        })
        .WithName("ListSeriesNarrationRebuilds")
        .Produces<IReadOnlyList<SeriesNarrationRebuildResponse>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{batchId:guid}/retry", async (
            Guid seriesId, Guid batchId, CreateSeriesNarrationRebuildRequest request,
            ISeriesNarrationService service, CancellationToken cancellationToken) =>
        {
            var batch = await service.RetryRebuildAsync(seriesId, batchId, request, cancellationToken);
            return batch is null ? Results.NotFound() : Results.Ok(batch);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("RetrySeriesNarrationRebuild")
        .Produces<SeriesNarrationRebuildResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
            Guid seriesId,
            CreateSeriesNarrationRebuildRequest request,
            HttpContext httpContext,
            ISeriesNarrationService service,
            CancellationToken cancellationToken) =>
        {
            var batch = await service.CreateRebuildAsync(seriesId, request, cancellationToken);
            return batch is null
                ? Results.NotFound()
                : Results.Created(
                    $"{httpContext.Request.PathBase}/api/series/{seriesId}/narration-rebuilds/{batch.Id}",
                    batch);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("CreateSeriesNarrationRebuild")
        .Produces<SeriesNarrationRebuildResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{batchId:guid}", async (
            Guid seriesId,
            Guid batchId,
            HttpContext httpContext,
            ISeriesNarrationService service,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "private, no-store";
            var batch = await service.GetRebuildAsync(seriesId, batchId, cancellationToken);
            return batch is null ? Results.NotFound() : Results.Ok(batch);
        })
        .WithName("GetSeriesNarrationRebuild")
        .Produces<SeriesNarrationRebuildResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{batchId:guid}/discard", async (
            Guid seriesId,
            Guid batchId,
            ISeriesNarrationService service,
            CancellationToken cancellationToken) =>
        {
            var batch = await service.DiscardRebuildAsync(seriesId, batchId, cancellationToken);
            return batch is null ? Results.NotFound() : Results.Ok(batch);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("DiscardSeriesNarrationRebuild")
        .Produces<SeriesNarrationRebuildResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{batchId:guid}/activate", async (
            Guid seriesId,
            Guid batchId,
            ISeriesNarrationService service,
            CancellationToken cancellationToken) =>
        {
            var batch = await service.ActivateRebuildAsync(seriesId, batchId, cancellationToken);
            return batch is null ? Results.NotFound() : Results.Ok(batch);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("ActivateSeriesNarrationRebuild")
        .Produces<SeriesNarrationRebuildResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }
}

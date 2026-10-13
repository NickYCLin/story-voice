using StoryVoice.Application.Series;

namespace StoryVoice.Api;

public static class SeriesEndpoints
{
    public static IEndpointRouteBuilder MapSeriesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/series")
            .WithTags("Series")
            .RequireAuthorization(StoryVoicePolicies.UserSession);

        group.MapGet("/", async (
            ISeriesService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)))
            .WithName("ListStorySeries");

        group.MapGet("/voice-options", async (ISeriesService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListVoiceOptionsAsync(cancellationToken)))
            .WithName("ListSeriesVoiceOptions");

        group.MapPost("/voice-preview", async (
            SeriesVoicePreviewRequest request,
            HttpContext httpContext,
            ISeriesVoicePreviewService service,
            CancellationToken cancellationToken) =>
        {
            var preview = await service.GenerateAsync(request, cancellationToken);
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            httpContext.Response.Headers.Append("X-BlueMagpie-Model-Revision", preview.ModelRevision);
            httpContext.Response.Headers.Append("X-BlueMagpie-Voice", preview.Voice);
            return Results.File(preview.Content, preview.ContentType);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("PreviewSeriesVoice");

        group.MapGet("/{seriesId:guid}", async (
            Guid seriesId,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.GetAsync(seriesId, cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .WithName("GetStorySeries");

        group.MapGet("/{seriesId:guid}/characters/{characterId:guid}/local-clone-preview", async (
            Guid seriesId,
            Guid characterId,
            HttpContext httpContext,
            ILocalClonePreviewService service,
            CancellationToken cancellationToken) =>
        {
            var availability = await service.GetAvailabilityAsync(
                seriesId,
                characterId,
                cancellationToken);
            httpContext.Response.Headers.CacheControl = "private, no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            return availability is null ? Results.NotFound() : Results.Ok(availability);
        })
        .WithName("GetLocalClonePreviewAvailability");

        group.MapPost("/{seriesId:guid}/characters/{characterId:guid}/local-clone-preview", async (
            Guid seriesId,
            Guid characterId,
            LocalClonePreviewRequest request,
            HttpContext httpContext,
            ILocalClonePreviewService service,
            CancellationToken cancellationToken) =>
        {
            var preview = await service.PreviewAsync(
                seriesId,
                characterId,
                request,
                cancellationToken);
            if (preview is null)
            {
                return Results.NotFound();
            }

            httpContext.Response.Headers.CacheControl = "private, no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            httpContext.Response.ContentLength = preview.Content.LongLength;
            return Results.File(preview.Content, "audio/wav");
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("PreviewLocalCloneCharacterVoice");

        group.MapPost("/", async (
            CreateStorySeriesRequest request,
            HttpContext httpContext,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.CreateAsync(request, cancellationToken);
            return Results.Created(
                $"{httpContext.Request.PathBase}/api/series/{series.Id}",
                series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("CreateStorySeries");

        group.MapPost("/{seriesId:guid}/books", async (
            Guid seriesId,
            AddSeriesBookRequest request,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.AddBookAsync(seriesId, request, cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("AddStorySeriesBook");

        group.MapPost("/{seriesId:guid}/characters", async (
            Guid seriesId,
            AddSeriesCharacterRequest request,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.AddCharacterAsync(seriesId, request, cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("AddStorySeriesCharacter");

        group.MapPut("/{seriesId:guid}/characters/{characterId:guid}", async (
            Guid seriesId,
            Guid characterId,
            UpdateSeriesCharacterRequest request,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.UpdateCharacterAsync(
                seriesId,
                characterId,
                request,
                cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("UpdateStorySeriesCharacter");

        group.MapPut("/{seriesId:guid}/characters/{characterId:guid}/character-profile", async (
            Guid seriesId,
            Guid characterId,
            SetSeriesCharacterProfileRequest request,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.SetCharacterProfileAsync(
                seriesId,
                characterId,
                request,
                cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("SetStorySeriesCharacterProfile");

        group.MapPut("/{seriesId:guid}/point-of-view-character", async (
            Guid seriesId,
            SetSeriesPointOfViewCharacterRequest request,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.SetPointOfViewCharacterAsync(seriesId, request, cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("SetStorySeriesPointOfViewCharacter");

        group.MapPut("/{seriesId:guid}/narrative-voice", async (
            Guid seriesId,
            ConfigureSeriesNarrativeVoiceRequest request,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.ConfigureNarrativeVoiceAsync(
                seriesId,
                request,
                cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("ConfigureStorySeriesNarrativeVoice");

        group.MapPut("/{seriesId:guid}/voices", async (
            Guid seriesId,
            ConfigureSeriesVoicesRequest request,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.ConfigureVoicesAsync(
                seriesId,
                request,
                cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("ConfigureStorySeriesVoices");

        group.MapPost("/{seriesId:guid}/characters/{characterId:guid}/aliases", async (
            Guid seriesId,
            Guid characterId,
            AddSeriesCharacterAliasRequest request,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.AddAliasAsync(
                seriesId,
                characterId,
                request,
                cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("AddStorySeriesCharacterAlias");

        group.MapPost("/{seriesId:guid}/analyzed-characters", async (
            Guid seriesId,
            ApplyAnalyzedSeriesCharactersRequest request,
            ISeriesService service,
            CancellationToken cancellationToken) =>
        {
            var series = await service.ApplyAnalyzedCharactersAsync(
                seriesId,
                request,
                cancellationToken);
            return series is null ? Results.NotFound() : Results.Ok(series);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("ApplyAnalyzedStorySeriesCharacters");

        return endpoints;
    }
}

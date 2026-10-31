using StoryVoice.Application.VoiceCatalog;

namespace StoryVoice.Api;

public static class PublicVoiceCatalogEndpoints
{
    private const string WaveContentType = "audio/wav";

    public static IEndpointRouteBuilder MapPublicVoiceCatalogEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/public/v1/voices", GetVoicesAsync)
            .AllowAnonymous()
            .WithTags("Public voice catalog")
            .WithName("GetPublicVoiceCatalog")
            .WithSummary("List publicly licensed StoryVoice character voices")
            .Produces<IReadOnlyList<PublicVoiceCatalogCard>>(StatusCodes.Status200OK);

        endpoints.MapGet("/api/public/v1/voices/{alias}", GetVoiceAsync)
            .AllowAnonymous()
            .WithTags("Public voice catalog")
            .WithName("GetPublicVoiceDetail")
            .WithSummary("Read a public voice and its current license summary")
            .Produces<PublicVoiceCatalogDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/public/v1/voices/{alias}/demo", GetDemoAsync)
            .AllowAnonymous()
            .WithTags("Public voice catalog")
            .WithName("GetPublicVoiceDemo")
            .WithSummary("Play a fixed, pre-generated public voice demo")
            .Produces(StatusCodes.Status200OK, contentType: WaveContentType)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetVoicesAsync(
        HttpContext httpContext,
        IPublicVoiceCatalogService catalogService,
        CancellationToken cancellationToken)
    {
        ApplyNoStoreHeaders(httpContext.Response);
        var voices = await catalogService.GetVoicesAsync(cancellationToken);
        return Results.Ok(voices);
    }

    private static async Task<IResult> GetVoiceAsync(
        string alias,
        HttpContext httpContext,
        IPublicVoiceCatalogService catalogService,
        CancellationToken cancellationToken)
    {
        ApplyNoStoreHeaders(httpContext.Response);
        var voice = await catalogService.GetVoiceAsync(alias, cancellationToken);
        return voice is null ? Results.NotFound() : Results.Ok(voice);
    }

    private static async Task<IResult> GetDemoAsync(
        string alias,
        HttpContext httpContext,
        IPublicVoiceCatalogService catalogService,
        CancellationToken cancellationToken)
    {
        ApplyNoStoreHeaders(httpContext.Response);
        var demo = await catalogService.GetDemoAsync(alias, cancellationToken);
        if (demo is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.ContentLength = demo.Content.LongLength;
        return Results.Bytes(demo.Content, demo.ContentType);
    }

    private static void ApplyNoStoreHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
    }
}

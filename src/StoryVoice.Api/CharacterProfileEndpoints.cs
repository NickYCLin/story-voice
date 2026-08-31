using StoryVoice.Application.Characters;
using StoryVoice.Application.Series;

namespace StoryVoice.Api;

public static class CharacterProfileEndpoints
{
    private const long MaximumAvatarBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapCharacterProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/character-profiles")
            .WithTags("CharacterProfiles")
            .RequireAuthorization(StoryVoicePolicies.UserSession);

        group.MapGet("/", async (
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapGet("/{characterProfileId:guid}", async (
            Guid characterProfileId,
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
        {
            var profile = await service.GetAsync(characterProfileId, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapGet("/{characterProfileId:guid}/local-clone-preview", async (
            Guid characterProfileId,
            HttpContext httpContext,
            ILocalClonePreviewService service,
            CancellationToken cancellationToken) =>
        {
            var availability = await service.GetCharacterProfileAvailabilityAsync(
                characterProfileId,
                cancellationToken);
            httpContext.Response.Headers.CacheControl = "private, no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            return availability is null ? Results.NotFound() : Results.Ok(availability);
        })
        .WithName("GetCharacterProfileLocalClonePreviewAvailability");

        group.MapPost("/{characterProfileId:guid}/local-clone-preview", async (
            Guid characterProfileId,
            LocalClonePreviewRequest request,
            HttpContext httpContext,
            ILocalClonePreviewService service,
            CancellationToken cancellationToken) =>
        {
            var preview = await service.PreviewCharacterProfileAsync(
                characterProfileId,
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
        .WithName("PreviewCharacterProfileLocalCloneVoice");

        group.MapPost("/", async (
            CreateCharacterProfileRequest request,
            HttpContext httpContext,
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
        {
            var profile = await service.CreateAsync(request, cancellationToken);
            return Results.Created($"{httpContext.Request.PathBase}/api/character-profiles/{profile.Id}", profile);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/ai-assist", async (
            GenerateCharacterProfileAssistRequest request,
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GenerateAssistAsync(request, cancellationToken);
            return Results.Ok(result);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("GenerateCharacterProfileAssist");

        group.MapPut("/{characterProfileId:guid}", async (
            Guid characterProfileId,
            UpdateCharacterProfileRequest request,
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
        {
            var profile = await service.UpdateAsync(characterProfileId, request, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/{characterProfileId:guid}/avatar", async (
            Guid characterProfileId,
            IFormFile avatar,
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
        {
            if (avatar.Length == 0)
            {
                throw new ArgumentException("頭像檔案不可為空白。", nameof(avatar));
            }

            if (avatar.Length > MaximumAvatarBytes)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Avatar is too large",
                    detail: "頭像檔案不可超過 5 MiB。");
            }

            await using var content = avatar.OpenReadStream();
            var profile = await service.SetAvatarAsync(
                characterProfileId,
                content,
                Path.GetFileName(avatar.FileName),
                cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .DisableAntiforgery()
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapGet("/{characterProfileId:guid}/avatar", async (
            Guid characterProfileId,
            HttpContext httpContext,
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
        {
            var avatar = await service.GetAvatarAsync(characterProfileId, cancellationToken);
            httpContext.Response.Headers.CacheControl = "private, no-store";
            httpContext.Response.Headers.XContentTypeOptions = "nosniff";
            return avatar is null
                ? Results.NotFound()
                : Results.File(avatar.AbsolutePath, avatar.ContentType, enableRangeProcessing: true);
        });

        group.MapDelete("/{characterProfileId:guid}", async (
            Guid characterProfileId,
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
        {
            var deleted = await service.DeleteAsync(characterProfileId, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/{characterProfileId:guid}/activate", async (
            Guid characterProfileId,
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
        {
            var profile = await service.SetActiveAsync(characterProfileId, true, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/{characterProfileId:guid}/deactivate", async (
            Guid characterProfileId,
            ICharacterProfileService service,
            CancellationToken cancellationToken) =>
        {
            var profile = await service.SetActiveAsync(characterProfileId, false, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }
}

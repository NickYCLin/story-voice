using StoryVoice.Application.Narrations.SpeechPlanning;

namespace StoryVoice.Api;

public static class SpeechPlanEndpoints
{
    public static IEndpointRouteBuilder MapSpeechPlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/series/{seriesId:guid}")
            .WithTags("SpeechPlans")
            .RequireAuthorization(StoryVoicePolicies.UserSession);

        group.MapGet("/books/{bookId:guid}/chapters/{chapterId:guid}/speech-plan", async (
            Guid seriesId,
            Guid bookId,
            Guid chapterId,
            ISpeechPlanService service,
            CancellationToken cancellationToken) =>
        {
            var draft = await service.GetDraftAsync(seriesId, bookId, chapterId, cancellationToken);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        })
        .WithName("GetChapterSpeechPlanDraft");

        group.MapPost("/books/{bookId:guid}/chapters/{chapterId:guid}/speech-plan", async (
            Guid seriesId,
            Guid bookId,
            Guid chapterId,
            ISpeechPlanService service,
            CancellationToken cancellationToken) =>
        {
            var draft = await service.BuildOrRefreshDraftAsync(seriesId, bookId, chapterId, cancellationToken);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("BuildOrRefreshChapterSpeechPlanDraft");

        group.MapPut("/speech-plan-drafts/{draftId:guid}/segments/{segmentId:guid}/confirm", async (
            Guid seriesId,
            Guid draftId,
            Guid segmentId,
            ConfirmSpeechSegmentRequest request,
            ISpeechPlanService service,
            CancellationToken cancellationToken) =>
        {
            var draft = await service.ConfirmSegmentAsync(seriesId, draftId, segmentId, request, cancellationToken);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("ConfirmSpeechPlanSegment");

        group.MapPut("/speech-plan-drafts/{draftId:guid}/segments/{segmentId:guid}/reject", async (
            Guid seriesId,
            Guid draftId,
            Guid segmentId,
            ISpeechPlanService service,
            CancellationToken cancellationToken) =>
        {
            var draft = await service.RejectSegmentAsync(seriesId, draftId, segmentId, cancellationToken);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("RejectSpeechPlanSegment");

        group.MapPut("/speech-plan-drafts/{draftId:guid}/segments/{segmentId:guid}/reassign", async (
            Guid seriesId,
            Guid draftId,
            Guid segmentId,
            ReassignSpeechSegmentRequest request,
            ISpeechPlanService service,
            CancellationToken cancellationToken) =>
        {
            var draft = await service.ReassignSegmentAsync(seriesId, draftId, segmentId, request, cancellationToken);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("ReassignSpeechPlanSegment");

        group.MapPost("/speech-plan-drafts/{draftId:guid}/segments/confirm-suggested", async (
            Guid seriesId,
            Guid draftId,
            ConfirmSuggestedSpeechSegmentsRequest request,
            ISpeechPlanService service,
            CancellationToken cancellationToken) =>
        {
            var draft = await service.ConfirmSuggestedSegmentsAsync(seriesId, draftId, request, cancellationToken);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("ConfirmSuggestedSpeechPlanSegments");

        group.MapPost("/speech-plan-drafts/{draftId:guid}/segments/{segmentId:guid}/preview", async (
            Guid seriesId,
            Guid draftId,
            Guid segmentId,
            HttpContext httpContext,
            ISpeechSegmentPreviewService service,
            CancellationToken cancellationToken) =>
        {
            var preview = await service.PreviewSegmentAsync(seriesId, draftId, segmentId, cancellationToken);
            if (preview is null)
            {
                return Results.NotFound();
            }

            httpContext.Response.Headers.CacheControl = "private, no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            httpContext.Response.ContentLength = preview.Content.LongLength;
            return Results.File(preview.Content, preview.ContentType);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("PreviewSpeechPlanSegment");

        group.MapPost("/speech-plan-drafts/{draftId:guid}/confirm", async (
            Guid seriesId,
            Guid draftId,
            ISpeechPlanService service,
            CancellationToken cancellationToken) =>
        {
            var revision = await service.ConfirmPlanAsync(seriesId, draftId, cancellationToken);
            return revision is null ? Results.NotFound() : Results.Ok(revision);
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("ConfirmSpeechPlan");

        return endpoints;
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryVoice.Application.Books;
using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Series;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class SpeechPlanApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Speech_plan_endpoints_require_authentication_and_mutations_require_csrf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var anonymousClient = factory.CreateClient();
        var seriesId = Guid.NewGuid();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        using var anonymousResponse = await anonymousClient.GetAsync(
            $"/api/series/{seriesId}/books/{bookId}/chapters/{chapterId}/speech-plan",
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var authenticatedClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var missingCsrfResponse = await authenticatedClient.PostAsJsonAsync(
            $"/api/series/{seriesId}/books/{bookId}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);
    }

    [Fact]
    public async Task Building_a_draft_auto_confirms_an_exact_reporting_clause_and_the_whole_plan_can_be_confirmed()
    {
        const string chapterTitle = "序章";
        const string chapterBody = "「你回來了？」艾莉絲說。她轉過身，慢慢地走向窗邊。";
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var series = await CreateSeriesAsync(client, "劇本審核測試系列", cancellationToken);
        var book = await CreateBookAsync(client, chapterTitle, chapterBody, cancellationToken);
        await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);
        var character = await AddCharacterAsync(client, series.Id, "艾莉絲", cancellationToken);
        var chapterId = book.Chapters.Single().Id;

        using var buildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        Assert.True(
            buildResponse.StatusCode == HttpStatusCode.OK,
            $"Unexpected response: {await buildResponse.Content.ReadAsStringAsync(cancellationToken)}");
        var draft = await buildResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.NotNull(draft);
        Assert.True(draft.Segments.Count >= 2);
        Assert.Equal("ChapterTitle", draft.Segments[0].SourceKind);
        Assert.Equal("Narrator", draft.Segments[0].Kind);
        Assert.Equal("Confirmed", draft.Segments[0].ReviewStatus);

        var dialogueSegment = Assert.Single(draft.Segments, segment => segment.Kind == "Dialogue");
        Assert.Equal(character.Id, dialogueSegment.CharacterId);
        Assert.Equal("Confirmed", dialogueSegment.ReviewStatus);
        Assert.Equal("Rule", dialogueSegment.DecisionSource);
        Assert.True(dialogueSegment.Confidence >= 90);
        Assert.Equal("ReadyToConfirm", draft.Status);

        using var getResponse = await client.GetAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            cancellationToken);
        var responseText = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.DoesNotContain(chapterBody, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("你回來了", responseText, StringComparison.Ordinal);

        using var confirmResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/confirm",
            new { },
            cancellationToken);
        var revision = await confirmResponse.Content
            .ReadFromJsonAsync<ConfirmedSpeechPlanRevisionResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        Assert.NotNull(revision);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal(draft.Segments.Count, revision.SegmentCount);
        Assert.False(string.IsNullOrWhiteSpace(revision.Fingerprint));
    }

    [Fact]
    public async Task Ambiguous_dialogue_needs_manual_confirmation_before_the_plan_can_be_confirmed()
    {
        const string chapterTitle = "第一章";
        const string chapterBody = "他站在門口。「快走。」她低聲說了什麼。";
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var series = await CreateSeriesAsync(client, "手動審核系列", cancellationToken);
        var book = await CreateBookAsync(client, chapterTitle, chapterBody, cancellationToken);
        await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);
        var character = await AddCharacterAsync(client, series.Id, "鮑伯", cancellationToken);
        var chapterId = book.Chapters.Single().Id;

        using var buildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        var draft = await buildResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.NotNull(draft);
        var dialogueSegment = Assert.Single(draft.Segments, segment => segment.Kind == "Dialogue");
        Assert.Equal("Suggested", dialogueSegment.ReviewStatus);
        Assert.Null(dialogueSegment.CharacterId);
        Assert.Equal("NeedsReview", draft.Status);

        using var prematureConfirmResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/confirm",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, prematureConfirmResponse.StatusCode);

        using var confirmSegmentRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/segments/{dialogueSegment.Id}/confirm")
        {
            Content = JsonContent.Create(new ConfirmSpeechSegmentRequest(character.Id)),
        };
        using var confirmSegmentResponse = await client.SendWithCsrfAsync(confirmSegmentRequest, cancellationToken);
        var updatedDraft = await confirmSegmentResponse.Content
            .ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmSegmentResponse.StatusCode);
        Assert.NotNull(updatedDraft);
        Assert.Equal("ReadyToConfirm", updatedDraft.Status);
        var updatedSegment = Assert.Single(updatedDraft.Segments, segment => segment.Kind == "Dialogue");
        Assert.Equal(character.Id, updatedSegment.CharacterId);
        Assert.Equal("Confirmed", updatedSegment.ReviewStatus);
        Assert.Equal("User", updatedSegment.DecisionSource);

        using var rebuildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        var rebuiltDraft = await rebuildResponse.Content
            .ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, rebuildResponse.StatusCode);
        Assert.NotNull(rebuiltDraft);
        var preservedConfirmation = Assert.Single(
            rebuiltDraft.Segments,
            segment => segment.Kind == "Dialogue");
        Assert.Equal(character.Id, preservedConfirmation.CharacterId);
        Assert.Equal("Confirmed", preservedConfirmation.ReviewStatus);
        Assert.Equal("User", preservedConfirmation.DecisionSource);
        Assert.Equal(100, preservedConfirmation.Confidence);

        using var rejectSegmentRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/series/{series.Id}/speech-plan-drafts/{rebuiltDraft.Id}/segments/{preservedConfirmation.Id}/reject");
        using var rejectSegmentResponse = await client.SendWithCsrfAsync(
            rejectSegmentRequest,
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, rejectSegmentResponse.StatusCode);

        using var rebuildRejectedResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        var rebuiltRejectedDraft = await rebuildRejectedResponse.Content
            .ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, rebuildRejectedResponse.StatusCode);
        Assert.NotNull(rebuiltRejectedDraft);
        var preservedRejection = Assert.Single(
            rebuiltRejectedDraft.Segments,
            segment => segment.Kind == "Dialogue");
        Assert.Null(preservedRejection.CharacterId);
        Assert.Equal("Rejected", preservedRejection.ReviewStatus);
        Assert.Equal("User", preservedRejection.DecisionSource);
        Assert.Equal(0, preservedRejection.Confidence);

        using var reconfirmSegmentRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/series/{series.Id}/speech-plan-drafts/{rebuiltRejectedDraft.Id}/segments/{preservedRejection.Id}/confirm")
        {
            Content = JsonContent.Create(new ConfirmSpeechSegmentRequest(character.Id)),
        };
        using var reconfirmSegmentResponse = await client.SendWithCsrfAsync(
            reconfirmSegmentRequest,
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, reconfirmSegmentResponse.StatusCode);

        using var confirmResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/speech-plan-drafts/{rebuiltRejectedDraft.Id}/confirm",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
    }

    [Fact]
    public async Task Point_of_view_narrative_mode_maps_title_and_all_raw_narration_after_dialogue_attribution()
    {
        const string chapterTitle = "第一章 主角視角";
        const string chapterBody = "主角走進教室。主角說：「你好。」我心想：『這不可能。』窗外一片安靜。";
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var series = await CreateSeriesAsync(client, "主角視角劇本系列", cancellationToken);
        var book = await CreateBookAsync(client, chapterTitle, chapterBody, cancellationToken);
        await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);
        var pointOfViewCharacter = await AddCharacterAsync(client, series.Id, "主角", cancellationToken);
        using var configureResponse = await client.PutWithCsrfAsync(
            $"/api/series/{series.Id}/narrative-voice",
            new ConfigureSeriesNarrativeVoiceRequest(
                NarrativeVoiceMode.PointOfViewInnerMonologue,
                pointOfViewCharacter.Id),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, configureResponse.StatusCode);

        var chapterId = book.Chapters.Single().Id;
        using var buildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        var draft = await buildResponse.Content
            .ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, buildResponse.StatusCode);
        Assert.NotNull(draft);
        Assert.DoesNotContain(draft.Segments, segment => segment.Kind == "Narrator");
        var title = draft.Segments[0];
        Assert.Equal("ChapterTitle", title.SourceKind);
        Assert.Equal("InnerMonologue", title.Kind);
        Assert.Equal(pointOfViewCharacter.Id, title.CharacterId);

        var dialogue = Assert.Single(draft.Segments, segment => segment.Kind == "Dialogue");
        Assert.Equal(pointOfViewCharacter.Id, dialogue.CharacterId);
        Assert.Equal("Confirmed", dialogue.ReviewStatus);
        Assert.Equal("Rule", dialogue.DecisionSource);

        var innerSegments = draft.Segments
            .Where(segment => segment.Kind == "InnerMonologue")
            .ToArray();
        Assert.True(innerSegments.Length >= 3);
        Assert.All(innerSegments, segment =>
        {
            Assert.Equal(pointOfViewCharacter.Id, segment.CharacterId);
            Assert.Equal(100, segment.Confidence);
            Assert.Equal("Rule", segment.DecisionSource);
            Assert.Equal("Confirmed", segment.ReviewStatus);
        });
        Assert.Equal("ReadyToConfirm", draft.Status);
    }

    [Fact]
    public async Task Point_of_view_narrative_mode_fails_closed_when_its_character_is_not_valid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var series = await CreateSeriesAsync(client, "無效主角視角系列", cancellationToken);
        var book = await CreateBookAsync(client, "第一章", "窗外一片安靜。", cancellationToken);
        await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var storedSeries = await dbContext.StorySeries.SingleAsync(
                candidate => candidate.Id == series.Id,
                cancellationToken);
            dbContext.Entry(storedSeries)
                .Property(candidate => candidate.NarrativeVoiceMode)
                .CurrentValue = NarrativeVoiceMode.PointOfViewInnerMonologue;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var chapterId = book.Chapters.Single().Id;
        using var buildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, buildResponse.StatusCode);
        var problem = await buildResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("requires a valid series character", problem, StringComparison.Ordinal);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        Assert.False(await verificationDb.ChapterSpeechPlanDrafts.AnyAsync(
            draft => draft.SeriesId == series.Id,
            cancellationToken));
    }

    [Fact]
    public async Task Written_title_is_confirmed_as_point_of_view_inner_monologue_not_dialogue()
    {
        const string chapterTitle = "第一章";
        const string chapterBody = "最厚的一迭有用活頁夾子整理起來，叫做『新生入學介紹與如何自保』。我繼續往下看。";
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var series = await CreateSeriesAsync(client, "內心默讀測試系列", cancellationToken);
        var book = await CreateBookAsync(client, chapterTitle, chapterBody, cancellationToken);
        await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);
        var pointOfViewCharacter = await AddCharacterAsync(client, series.Id, "主角", cancellationToken);
        var chapterId = book.Chapters.Single().Id;
        using var initialBuildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        var initialDraft = await initialBuildResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, initialBuildResponse.StatusCode);
        Assert.NotNull(initialDraft);
        Assert.DoesNotContain(initialDraft.Segments, segment => segment.Kind == "InnerMonologue");
        Assert.Contains(
            initialDraft.Segments,
            segment => segment.Kind == "Narrator" && segment.StartOffset == chapterBody.IndexOf('『'));
        using var initialConfirmResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/speech-plan-drafts/{initialDraft.Id}/confirm",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, initialConfirmResponse.StatusCode);

        using var pointOfViewResponse = await client.PutWithCsrfAsync(
            $"/api/series/{series.Id}/point-of-view-character",
            new SetSeriesPointOfViewCharacterRequest(pointOfViewCharacter.Id),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, pointOfViewResponse.StatusCode);

        using var buildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        var draft = await buildResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, buildResponse.StatusCode);
        Assert.NotNull(draft);
        Assert.Equal(initialDraft.PlanVersion + 1, draft.PlanVersion);
        Assert.Null(draft.ConfirmedRevisionId);
        var innerMonologue = Assert.Single(draft.Segments, segment => segment.Kind == "InnerMonologue");
        Assert.Equal(pointOfViewCharacter.Id, innerMonologue.CharacterId);
        Assert.Equal(pointOfViewCharacter.CanonicalName, innerMonologue.CharacterName);
        Assert.Equal(100, innerMonologue.Confidence);
        Assert.Equal("Rule", innerMonologue.DecisionSource);
        Assert.Equal("Confirmed", innerMonologue.ReviewStatus);
        Assert.DoesNotContain(draft.Segments, segment => segment.Kind == "Dialogue");
        Assert.Equal("ReadyToConfirm", draft.Status);
    }

    [Fact]
    public async Task Non_owner_and_non_member_book_cannot_touch_the_speech_plan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var strangerClient = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var series = await CreateSeriesAsync(ownerClient, "隔離測試系列", cancellationToken);
        var book = await CreateBookAsync(ownerClient, "章名", "「嗨。」他說。", cancellationToken);
        var chapterId = book.Chapters.Single().Id;

        using var notMemberResponse = await ownerClient.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, notMemberResponse.StatusCode);

        await ownerClient.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);

        using var strangerResponse = await strangerClient.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, strangerResponse.StatusCode);
    }

    [Fact]
    public async Task Bulk_confirm_accepts_only_suggestions_that_carry_a_character()
    {
        const string chapterTitle = "批次確認";
        const string chapterBody = "艾莉絲轉過身。「快走。」他站在門口。「安靜。」";
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var series = await CreateSeriesAsync(client, "批次確認系列", cancellationToken);
        var book = await CreateBookAsync(client, chapterTitle, chapterBody, cancellationToken);
        await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);
        var character = await AddCharacterAsync(client, series.Id, "艾莉絲", cancellationToken);
        var chapterId = book.Chapters.Single().Id;

        using var buildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        var draft = await buildResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.NotNull(draft);
        var dialogueSegments = draft.Segments.Where(segment => segment.Kind == "Dialogue").ToArray();
        Assert.Equal(2, dialogueSegments.Length);
        Assert.Equal(character.Id, dialogueSegments[0].CharacterId);
        Assert.Equal("Suggested", dialogueSegments[0].ReviewStatus);
        Assert.Null(dialogueSegments[1].CharacterId);
        Assert.Equal("Suggested", dialogueSegments[1].ReviewStatus);

        using var bulkResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/segments/confirm-suggested",
            new ConfirmSuggestedSpeechSegmentsRequest(character.Id),
            cancellationToken);
        var bulkDraft = await bulkResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, bulkResponse.StatusCode);
        Assert.NotNull(bulkDraft);
        var bulkDialogue = bulkDraft.Segments.Where(segment => segment.Kind == "Dialogue").ToArray();
        Assert.Equal("Confirmed", bulkDialogue[0].ReviewStatus);
        Assert.Equal("User", bulkDialogue[0].DecisionSource);
        Assert.Equal(character.Id, bulkDialogue[0].CharacterId);
        Assert.Equal("Suggested", bulkDialogue[1].ReviewStatus);
        Assert.Equal("NeedsReview", bulkDraft.Status);

        // A character-less suggestion must survive an accept-everything sweep: unknown speakers
        // never get silently promoted by a bulk action.
        using var sweepResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/segments/confirm-suggested",
            new ConfirmSuggestedSpeechSegmentsRequest(null),
            cancellationToken);
        var sweptDraft = await sweepResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, sweepResponse.StatusCode);
        Assert.NotNull(sweptDraft);
        Assert.Equal(
            "Suggested",
            sweptDraft.Segments.Where(segment => segment.Kind == "Dialogue").ToArray()[1].ReviewStatus);
    }

    [Fact]
    public async Task Reassigning_corrects_segment_kind_and_speaker_but_never_the_chapter_title()
    {
        const string chapterTitle = "改指派";
        const string chapterBody = "她轉過身。「你好。」";
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var series = await CreateSeriesAsync(client, "改指派系列", cancellationToken);
        var book = await CreateBookAsync(client, chapterTitle, chapterBody, cancellationToken);
        await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);
        var character = await AddCharacterAsync(client, series.Id, "艾莉絲", cancellationToken);
        var chapterId = book.Chapters.Single().Id;

        using var buildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        var draft = await buildResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.NotNull(draft);
        var narratorSegment = Assert.Single(
            draft.Segments,
            segment => segment.Kind == "Narrator" && segment.SourceKind == "Body");

        using var reassignRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/segments/{narratorSegment.Id}/reassign")
        {
            Content = JsonContent.Create(new ReassignSpeechSegmentRequest("Dialogue", character.Id)),
        };
        using var reassignResponse = await client.SendWithCsrfAsync(reassignRequest, cancellationToken);
        var reassignedDraft = await reassignResponse.Content
            .ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, reassignResponse.StatusCode);
        Assert.NotNull(reassignedDraft);
        var reassigned = Assert.Single(reassignedDraft.Segments, segment => segment.Id == narratorSegment.Id);
        Assert.Equal("Dialogue", reassigned.Kind);
        Assert.Equal(character.Id, reassigned.CharacterId);
        Assert.Equal("User", reassigned.DecisionSource);
        Assert.Equal("Confirmed", reassigned.ReviewStatus);

        using var titleRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/segments/{draft.Segments[0].Id}/reassign")
        {
            Content = JsonContent.Create(new ReassignSpeechSegmentRequest("Dialogue", character.Id)),
        };
        using var titleResponse = await client.SendWithCsrfAsync(titleRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, titleResponse.StatusCode);

        using var invalidKindRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/segments/{narratorSegment.Id}/reassign")
        {
            Content = JsonContent.Create(new ReassignSpeechSegmentRequest("InnerMonologue", null)),
        };
        using var invalidKindResponse = await client.SendWithCsrfAsync(invalidKindRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidKindResponse.StatusCode);
    }

    [Fact]
    public async Task Segment_preview_reports_unsupported_cloud_providers_instead_of_synthesizing()
    {
        const string chapterTitle = "試聽";
        const string chapterBody = "窗外一片安靜。";
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var series = await CreateSeriesAsync(client, "試聽系列", cancellationToken);
        var book = await CreateBookAsync(client, chapterTitle, chapterBody, cancellationToken);
        await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);
        var chapterId = book.Chapters.Single().Id;

        using var buildResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapterId}/speech-plan",
            new { },
            cancellationToken);
        var draft = await buildResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(cancellationToken);
        Assert.NotNull(draft);

        // The series narrator uses the Edge provider, which only synthesizes inside batch worker
        // jobs — preview must fail closed with a stable code instead of guessing another voice.
        using var previewResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/segments/{draft.Segments[0].Id}/preview",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, previewResponse.StatusCode);
        var problem = await previewResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("speech_segment_preview_unsupported_provider", problem, StringComparison.Ordinal);

        using var missingSegmentResponse = await client.PostWithCsrfAsync(
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/segments/{Guid.NewGuid()}/preview",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missingSegmentResponse.StatusCode);
    }

    private static async Task<StorySeriesDetailsResponse> CreateSeriesAsync(
        HttpClient client,
        string name,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostWithCsrfAsync(
            "/api/series",
            new
            {
                name,
                narratorProvider = "edge",
                narratorVoice = "zh-TW-YunJheNeural",
                narratorRate = "-5%",
                narratorPitch = "+0Hz",
                narratorVolume = "+0%",
                defaultSpeakerPauseMs = 350,
            },
            cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<StorySeriesDetailsResponse>(created);
    }

    private static async Task<StorySeriesCharacterResponse> AddCharacterAsync(
        HttpClient client,
        Guid seriesId,
        string canonicalName,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostWithCsrfAsync(
            $"/api/series/{seriesId}/characters",
            new
            {
                canonicalName,
                role = "Main",
                voiceProvider = "edge",
                voice = "zh-TW-HsiaoChenNeural",
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null,
            },
            cancellationToken);
        var details = await response.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(details);
        return details.Characters.Single(candidate => candidate.CanonicalName == canonicalName);
    }

    private static async Task<BookDetailsResponse> CreateBookAsync(
        HttpClient client,
        string chapterTitle,
        string chapterBody,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostWithCsrfAsync(
            "/api/books",
            new CreateBookRequest(
                $"Synthetic book {Guid.NewGuid():N}",
                "Synthetic author",
                "zh-TW",
                "synthetic.txt",
                [new CreateChapterRequest(1, chapterTitle, chapterBody)]),
            cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<BookDetailsResponse>(created);
    }
}

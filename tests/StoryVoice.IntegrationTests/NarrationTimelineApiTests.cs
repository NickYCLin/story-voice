using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryVoice.Application.Books;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class NarrationTimelineApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Timeline_returns_chapters_synced_text_and_stays_owner_scoped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var other = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(owner, cancellationToken);
        var chapter = await LoadSingleChapterAsync(book.Id, cancellationToken);
        var characterId = Guid.NewGuid();

        var job = await SeedCompletedJobAsync(book.Id, sourceHashFromChapters: true, cancellationToken);
        await SeedTimelineAsync(job, chapter, characterId, cancellationToken);

        using var response = await owner.GetAsync($"/api/narrations/{job.Id}/timeline", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var timeline = await response.Content.ReadFromJsonAsync<NarrationTimelineResponse>(cancellationToken);
        Assert.NotNull(timeline);
        Assert.True(timeline.TextAvailable);

        var chapterEntry = Assert.Single(timeline.Chapters);
        Assert.Equal(chapter.Id, chapterEntry.ChapterId);
        Assert.Equal(chapter.Title, chapterEntry.Title);
        Assert.Equal(0, chapterEntry.StartMs);

        Assert.Equal(2, timeline.Turns.Count);
        Assert.Equal("narrator", timeline.Turns[0].Kind);
        Assert.Null(timeline.Turns[0].CharacterId);
        Assert.Equal(chapter.Title, timeline.Turns[0].Text);
        Assert.Equal("dialogue", timeline.Turns[1].Kind);
        Assert.Equal(characterId, timeline.Turns[1].CharacterId);
        Assert.Equal(chapter.OriginalText[..4], timeline.Turns[1].Text);
        Assert.Equal(1_200, timeline.Turns[1].StartMs);

        using var otherResponse = await other.GetAsync($"/api/narrations/{job.Id}/timeline", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherResponse.StatusCode);
    }

    [Fact]
    public async Task Timeline_is_not_found_when_no_timeline_was_stored_for_the_job()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(owner, cancellationToken);
        var job = await SeedCompletedJobAsync(book.Id, sourceHashFromChapters: true, cancellationToken);

        using var response = await owner.GetAsync($"/api/narrations/{job.Id}/timeline", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Timeline_keeps_timing_but_withholds_text_when_the_source_no_longer_matches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(owner, cancellationToken);
        var chapter = await LoadSingleChapterAsync(book.Id, cancellationToken);

        // The job was composed from a different (older) snapshot of the text.
        var job = await SeedCompletedJobAsync(book.Id, sourceHashFromChapters: false, cancellationToken);
        await SeedTimelineAsync(job, chapter, Guid.NewGuid(), cancellationToken);

        using var response = await owner.GetAsync($"/api/narrations/{job.Id}/timeline", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var timeline = await response.Content.ReadFromJsonAsync<NarrationTimelineResponse>(cancellationToken);
        Assert.NotNull(timeline);
        Assert.False(timeline.TextAvailable);
        Assert.All(timeline.Turns, turn => Assert.Null(turn.Text));
        Assert.Equal(1_200, timeline.Turns[1].StartMs);
        Assert.Equal(chapter.Title, Assert.Single(timeline.Chapters).Title);
    }

    private async Task<Chapter> LoadSingleChapterAsync(Guid bookId, CancellationToken cancellationToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        return await db.Chapters.AsNoTracking().SingleAsync(
            chapter => chapter.BookId == bookId,
            cancellationToken);
    }

    private async Task<NarrationJob> SeedCompletedJobAsync(
        Guid bookId,
        bool sourceHashFromChapters,
        CancellationToken cancellationToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var book = await db.Books
            .AsNoTracking()
            .Include(candidate => candidate.Chapters)
            .SingleAsync(candidate => candidate.Id == bookId, cancellationToken);
        var sourceHash = sourceHashFromChapters
            ? NarrationSource.Create(book.Chapters.Select(chapter =>
                new NarrationChapterSource(chapter.Id, chapter.SortOrder, chapter.Title, chapter.OriginalText))).SourceHash
            : $"stale-{Guid.NewGuid():N}";
        var job = NarrationJob.Create(
            book.OwnerId!.Value,
            bookId,
            bookId,
            sourceHash,
            "timeline-test-voice",
            "timeline-test-rate",
            DateTimeOffset.UtcNow);
        job.Claim("timeline-test-worker", DateTimeOffset.UtcNow.AddMinutes(20));
        job.Complete(Path.Combine(book.OwnerId.Value.ToString("N"), $"{job.Id:N}.mp3"), 4);
        db.NarrationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private async Task SeedTimelineAsync(
        NarrationJob job,
        Chapter chapter,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var document = new NarrationTimelineDocument(
            NarrationTimelineDocument.CurrentSchemaVersion,
            [
                new NarrationTimelineTurn(
                    0,
                    1_200,
                    chapter.Id,
                    chapter.SortOrder,
                    ChapterStart: true,
                    [
                        new NarrationTimelineSlice(
                            NarrationTimelineSourceKinds.ChapterTitle,
                            0,
                            chapter.Title.Length,
                            NarrationTimelineTurnKinds.Narrator,
                            null),
                    ]),
                new NarrationTimelineTurn(
                    1_200,
                    2_400,
                    chapter.Id,
                    chapter.SortOrder,
                    ChapterStart: false,
                    [
                        new NarrationTimelineSlice(
                            NarrationTimelineSourceKinds.ChapterBody,
                            0,
                            4,
                            NarrationTimelineTurnKinds.Dialogue,
                            characterId),
                    ]),
            ]);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        db.NarrationTimelines.Add(NarrationTimeline.Create(job.OwnerId, job.Id, document.ToJson()));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<BookDetailsResponse> ImportTextAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("第一章\n這是合法測試正文，只用來驗證語音時間軸。"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", $"timeline-{Guid.NewGuid():N}.txt");
        using var response = await client.PostMultipartWithCsrfAsync("/api/books/import", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Import response did not contain a book.");
    }
}

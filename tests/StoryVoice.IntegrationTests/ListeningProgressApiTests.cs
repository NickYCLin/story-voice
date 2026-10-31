using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryVoice.Application.Books;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class ListeningProgressApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Progress_is_durable_owner_scoped_and_rejects_stale_tabs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(ct);
        using var other = await factory.CreateAuthenticatedClientAsync(ct);
        using var anonymous = factory.CreateCookieClient();
        var job = await SeedJobAsync(owner, completed: true, ct);
        var path = $"/api/narrations/{job.Id}/progress";
        var initial = await owner.GetFromJsonAsync<ListeningProgressResponse>(path, ct);
        Assert.NotNull(initial);
        Assert.Null(initial.Version);

        using var save = await owner.PutWithCsrfAsync(path, new SaveListeningProgressRequest(42_000, 120_000, null), ct);
        save.EnsureSuccessStatusCode();
        Assert.True(save.Headers.CacheControl?.NoStore);
        var saved = await save.Content.ReadFromJsonAsync<ListeningProgressResponse>(ct);
        Assert.NotNull(saved);
        Assert.NotNull(saved.Version);
        Assert.Equal(saved, await owner.GetFromJsonAsync<ListeningProgressResponse>(path, ct));

        using var stale = await owner.PutWithCsrfAsync(path, new SaveListeningProgressRequest(5_000, 120_000, null), ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(saved, await stale.Content.ReadFromJsonAsync<ListeningProgressResponse>(ct));

        using var completed = await owner.PutWithCsrfAsync(path, new SaveListeningProgressRequest(120_000, 120_000, saved.Version), ct);
        completed.EnsureSuccessStatusCode();
        using var late = await owner.PutWithCsrfAsync(path, new SaveListeningProgressRequest(50_000, 120_000, saved.Version), ct);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(120_000, (await owner.GetFromJsonAsync<ListeningProgressResponse>(path, ct))!.PositionMs);

        using var otherRead = await other.GetAsync(path, ct);
        using var otherWrite = await other.PutWithCsrfAsync(path, new SaveListeningProgressRequest(10, 100, null), ct);
        using var missingCsrf = await owner.PutAsJsonAsync(path, new SaveListeningProgressRequest(10, 100, null), ct);
        using var anonymousRead = await anonymous.GetAsync(path, ct);
        Assert.Equal(HttpStatusCode.NotFound, otherRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherWrite.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(101, 100)]
    [InlineData(0, 0)]
    [InlineData(0, ListeningProgress.MaximumDurationMs + 1)]
    public async Task Invalid_positions_are_not_saved(long position, long duration)
    {
        var ct = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(ct);
        var job = await SeedJobAsync(owner, completed: true, ct);
        var path = $"/api/narrations/{job.Id}/progress";
        using var response = await owner.PutWithCsrfAsync(path, new SaveListeningProgressRequest(position, duration, null), ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await owner.GetFromJsonAsync<ListeningProgressResponse>(path, ct))!.Version);
    }

    [Fact]
    public async Task Unfinished_historical_and_archived_audio_cannot_read_or_write_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(ct);
        var queued = await SeedJobAsync(owner, completed: false, ct);
        var historical = await SeedJobAsync(owner, completed: true, ct);
        var archived = await SeedJobAsync(owner, completed: true, ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var historicalJob = await db.NarrationJobs.SingleAsync(job => job.Id == historical.Id, ct);
            db.Entry(historicalJob).Property(job => job.Visibility).CurrentValue = NarrationArtifactVisibility.Historical;
            (await db.Books.SingleAsync(book => book.Id == archived.BookId, ct)).Archive();
            await db.SaveChangesAsync(ct);
        }
        foreach (var jobId in new[] { queued.Id, historical.Id, archived.Id, Guid.NewGuid() })
        {
            var path = $"/api/narrations/{jobId}/progress";
            using var read = await owner.GetAsync(path, ct);
            using var write = await owner.PutWithCsrfAsync(path, new SaveListeningProgressRequest(10, 100, null), ct);
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        }
    }

    private async Task<NarrationJob> SeedJobAsync(HttpClient owner, bool completed, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("第一章\n小雨在窗邊讀故事。")), "file", "progress.txt");
        using var imported = await owner.PostMultipartWithCsrfAsync("/api/books/import", content, ct);
        imported.EnsureSuccessStatusCode();
        var book = await imported.Content.ReadFromJsonAsync<BookDetailsResponse>(ct);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var entity = await db.Books.SingleAsync(item => item.Id == book!.Id, ct);
        var job = NarrationJob.Create(entity.OwnerId!.Value, entity.Id, entity.Id, "synthetic-source", "test-voice", "+0%", DateTimeOffset.UtcNow);
        if (completed)
        {
            job.Claim("test-worker", DateTimeOffset.UtcNow.AddMinutes(1));
            job.Complete($"{job.Id:N}.mp3", 100);
        }
        db.NarrationJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryVoice.Application.Books;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class NarrationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Usage_is_private_reports_unknown_interrupted_attempts_and_hides_archived_books()
    {
        var ct = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(ct);
        using var other = await factory.CreateAuthenticatedClientAsync(ct);
        var book = await ImportTextAsync(owner, ct);
        var job = await SeedQueuedPublishedAsync(book.Id, ct);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var running = await db.NarrationJobs.SingleAsync(item => item.Id == job.Id, ct);
            running.Claim("current-synthetic-lease", DateTimeOffset.UtcNow.AddMinutes(10));
            db.NarrationAttemptUsage.Add(NarrationAttemptUsage.Start(Guid.NewGuid(), job.OwnerId, job.Id, "old-synthetic-lease"));
            db.NarrationAttemptUsage.Add(NarrationAttemptUsage.Start(Guid.NewGuid(), job.OwnerId, job.Id, "current-synthetic-lease"));
            await db.SaveChangesAsync(ct);
        }
        using var response = await owner.GetAsync($"/api/narrations/{job.Id}/usage", ct);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("synthetic-lease", json);
        Assert.DoesNotContain("原始", json);
        var usage = await response.Content.ReadFromJsonAsync<NarrationUsageResponse>(ct);
        Assert.NotNull(usage);
        Assert.Equal(2, usage.TotalAttempts);
        Assert.Single(usage.Attempts, item => item.Outcome == "Unknown");
        Assert.Single(usage.Attempts, item => item.Outcome == "Running");
        Assert.All(usage.Attempts, item =>
        {
            Assert.Null(item.FinishedAt);
            Assert.Null(item.InputCharacters);
            Assert.Null(item.ElapsedMs);
        });
        using var denied = await other.GetAsync($"/api/narrations/{job.Id}/usage", ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var archivedBook = await db.Books.SingleAsync(item => item.Id == book.Id, ct);
            archivedBook.Archive();
            await db.SaveChangesAsync(ct);
        }
        using var archived = await owner.GetAsync($"/api/narrations/{job.Id}/usage", ct);
        Assert.Equal(HttpStatusCode.NotFound, archived.StatusCode);
    }

    [Fact]
    public async Task Usage_does_not_invent_history_for_existing_jobs_and_bounds_the_latest_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(ct);
        var book = await ImportTextAsync(owner, ct);
        var job = await SeedQueuedPublishedAsync(book.Id, ct);
        var empty = await owner.GetFromJsonAsync<NarrationUsageResponse>($"/api/narrations/{job.Id}/usage", ct);
        Assert.NotNull(empty);
        Assert.Equal(0, empty.TotalAttempts);
        Assert.Empty(empty.Attempts);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            for (var index = 0; index < 105; index++)
                db.NarrationAttemptUsage.Add(NarrationAttemptUsage.Start(Guid.NewGuid(), job.OwnerId, job.Id, "synthetic-lease"));
            await db.SaveChangesAsync(ct);
        }
        var bounded = await owner.GetFromJsonAsync<NarrationUsageResponse>($"/api/narrations/{job.Id}/usage", ct);
        Assert.NotNull(bounded);
        Assert.Equal(105, bounded.TotalAttempts);
        Assert.Equal(100, bounded.Attempts.Count);
        Assert.All(bounded.Attempts, item => Assert.Equal("Unknown", item.Outcome));
        Assert.Equal(bounded.Attempts.OrderByDescending(item => item.StartedAt).ThenByDescending(item => item.Id), bounded.Attempts);
    }

    [Fact]
    public async Task Legacy_single_voice_creation_is_retired_before_request_validation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var uploaded = await ImportTextAsync(client, cancellationToken);

        using var response = await client.PostWithCsrfAsync(
            $"/api/books/{uploaded.Id}/narrations/",
            new CreateNarrationRequest(false),
            cancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            SingleVoiceNarrationRetiredException.StableCode,
            problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Published_legacy_artifacts_are_owner_scoped_cancellable_and_range_streamed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        using var other = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(owner, cancellationToken);
        var first = await SeedQueuedPublishedAsync(book.Id, cancellationToken);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var job = await db.NarrationJobs.SingleAsync(item => item.Id == first.Id, cancellationToken);
            job.Claim("integration-worker", DateTimeOffset.UtcNow.AddMinutes(20));
            var relativePath = Path.Combine(job.OwnerId.ToString("N"), $"{job.Id:N}.mp3");
            var absolutePath = Path.Combine(factory.StorageRoot, "audio", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllBytesAsync(absolutePath, [1, 2, 3, 4, 5, 6, 7, 8], cancellationToken);
            job.Complete(relativePath, 8);
            await db.SaveChangesAsync(cancellationToken);
        }

        using var otherGet = await other.GetAsync($"/api/narrations/{first.Id}/", cancellationToken);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/narrations/{first.Id}/audio");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        using var rangeResponse = await owner.SendAsync(rangeRequest, cancellationToken);
        var rangeBytes = await rangeResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, otherGet.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal("audio/mpeg", rangeResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2, 3, 4], rangeBytes);

        var secondBook = await ImportTextAsync(owner, cancellationToken);
        var queued = await SeedQueuedPublishedAsync(secondBook.Id, cancellationToken);
        using var cancel = await owner.PostWithCsrfAsync(
            $"/api/narrations/{queued.Id}/cancel",
            new { },
            cancellationToken);
        var cancelled = await cancel.Content.ReadFromJsonAsync<NarrationJobResponse>(cancellationToken);
        Assert.NotNull(cancelled);
        Assert.Equal(NarrationJobStatus.Cancelled.ToString(), cancelled.Status);
    }

    [Fact]
    public async Task Regular_endpoints_hide_staged_and_historical_artifacts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(client, cancellationToken);
        var published = await SeedQueuedPublishedAsync(book.Id, cancellationToken);

        NarrationJob staged;
        NarrationJob queuedStaged;
        NarrationJob historical;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var ownerId = await db.Books
                .Where(candidate => candidate.Id == book.Id)
                .Select(candidate => candidate.OwnerId!.Value)
                .SingleAsync(cancellationToken);
            staged = NarrationArtifactTestData.CompletedStaged(ownerId, book.Id, book.Id);
            queuedStaged = NarrationArtifactTestData.QueuedStaged(ownerId, book.Id, book.Id);
            historical = NarrationArtifactTestData.CompletedHistorical(ownerId, book.Id, book.Id);
            db.NarrationJobs.AddRange(staged, queuedStaged, historical);
            await db.SaveChangesAsync(cancellationToken);
        }

        await NarrationArtifactTestData.WriteAudioAsync(factory.StorageRoot, staged, cancellationToken);
        await NarrationArtifactTestData.WriteAudioAsync(factory.StorageRoot, historical, cancellationToken);

        using var listResponse = await client.GetAsync(
            $"/api/books/{book.Id}/narrations/",
            cancellationToken);
        var listed = await listResponse.Content.ReadFromJsonAsync<NarrationJobResponse[]>(cancellationToken);
        Assert.NotNull(listed);
        Assert.Equal([published.Id], listed.Select(job => job.Id));

        foreach (var hidden in new[] { staged, queuedStaged, historical })
        {
            using var get = await client.GetAsync($"/api/narrations/{hidden.Id}/", cancellationToken);
            using var audio = await client.GetAsync($"/api/narrations/{hidden.Id}/audio", cancellationToken);
            using var cancel = await client.PostWithCsrfAsync(
                $"/api/narrations/{hidden.Id}/cancel",
                new { },
                cancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, audio.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var persisted = await db.NarrationJobs.AsNoTracking()
                .Where(job => job.Id == staged.Id
                    || job.Id == queuedStaged.Id
                    || job.Id == historical.Id)
                .ToDictionaryAsync(job => job.Id, cancellationToken);
            Assert.Equal(NarrationArtifactVisibility.Staged, persisted[staged.Id].Visibility);
            Assert.Equal(NarrationArtifactVisibility.Staged, persisted[queuedStaged.Id].Visibility);
            Assert.Equal(NarrationArtifactVisibility.Historical, persisted[historical.Id].Visibility);
            Assert.Equal(NarrationJobStatus.Completed, persisted[staged.Id].Status);
            Assert.Equal(NarrationJobStatus.Queued, persisted[queuedStaged.Id].Status);
            Assert.False(persisted[queuedStaged.Id].CancellationRequested);
            Assert.Equal(NarrationJobStatus.Completed, persisted[historical.Id].Status);
        }
    }

    [Fact]
    public async Task Legacy_creation_never_revives_a_historical_single_voice_artifact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(client, cancellationToken);
        var original = await SeedCompletedHistoricalAsync(book.Id, cancellationToken);

        using var repeatedCreate = await client.PostWithCsrfAsync(
            $"/api/books/{book.Id}/narrations/",
            new CreateNarrationRequest(true),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, repeatedCreate.StatusCode);
        using var problem = JsonDocument.Parse(await repeatedCreate.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal(
            SingleVoiceNarrationRetiredException.StableCode,
            problem.RootElement.GetProperty("code").GetString());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var persisted = await verifyDb.NarrationJobs.AsNoTracking()
            .SingleAsync(item => item.Id == original.Id, cancellationToken);
        Assert.Equal(NarrationArtifactVisibility.Historical, persisted.Visibility);
        Assert.Equal(NarrationJobStatus.Completed, persisted.Status);
    }

    [Fact]
    public async Task Concurrency_stamp_blocks_stale_completion_and_requeue_transitions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var firstBook = await ImportTextAsync(client, cancellationToken);
        var firstJob = await SeedQueuedPublishedAsync(firstBook.Id, cancellationToken);

        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var job = await setupDb.NarrationJobs.SingleAsync(item => item.Id == firstJob.Id, cancellationToken);
            job.Claim("worker-a:claim-a", DateTimeOffset.UtcNow.AddMinutes(20));
            await setupDb.SaveChangesAsync(cancellationToken);
        }

        await using (var cancelScope = factory.Services.CreateAsyncScope())
        await using (var staleWorkerScope = factory.Services.CreateAsyncScope())
        {
            var cancelDb = cancelScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var staleWorkerDb = staleWorkerScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var cancelJob = await cancelDb.NarrationJobs.SingleAsync(item => item.Id == firstJob.Id, cancellationToken);
            var staleWorkerJob = await staleWorkerDb.NarrationJobs.SingleAsync(item => item.Id == firstJob.Id, cancellationToken);

            cancelJob.RequestCancellation();
            await cancelDb.SaveChangesAsync(cancellationToken);
            staleWorkerJob.Complete("owner/stale.mp3", 42);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => staleWorkerDb.SaveChangesAsync(cancellationToken));
        }

        var secondBook = await ImportTextAsync(client, cancellationToken);
        var secondJob = await SeedQueuedPublishedAsync(secondBook.Id, cancellationToken);
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var job = await setupDb.NarrationJobs.SingleAsync(item => item.Id == secondJob.Id, cancellationToken);
            job.RequestCancellation();
            await setupDb.SaveChangesAsync(cancellationToken);
        }

        await using (var firstRequeueScope = factory.Services.CreateAsyncScope())
        await using (var staleRequeueScope = factory.Services.CreateAsyncScope())
        {
            var firstDb = firstRequeueScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var staleDb = staleRequeueScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var first = await firstDb.NarrationJobs.SingleAsync(item => item.Id == secondJob.Id, cancellationToken);
            var stale = await staleDb.NarrationJobs.SingleAsync(item => item.Id == secondJob.Id, cancellationToken);

            first.Requeue(DateTimeOffset.UtcNow);
            await firstDb.SaveChangesAsync(cancellationToken);
            stale.Requeue(DateTimeOffset.UtcNow);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => staleDb.SaveChangesAsync(cancellationToken));
        }
    }

    private async Task<NarrationJob> SeedQueuedPublishedAsync(Guid bookId, CancellationToken cancellationToken) =>
        await SeedNarrationAsync(bookId, bookId, NarrationArtifactTestData.QueuedPublished, cancellationToken);

    private async Task<NarrationJob> SeedCompletedHistoricalAsync(Guid bookId, CancellationToken cancellationToken) =>
        await SeedNarrationAsync(bookId, bookId, NarrationArtifactTestData.CompletedHistorical, cancellationToken);

    private async Task<NarrationJob> SeedNarrationAsync(
        Guid bookId,
        Guid contentBookId,
        Func<Guid, Guid, Guid, NarrationJob> create,
        CancellationToken cancellationToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var ownerId = await db.Books
            .Where(book => book.Id == bookId)
            .Select(book => book.OwnerId!.Value)
            .SingleAsync(cancellationToken);
        var job = create(ownerId, bookId, contentBookId);
        db.NarrationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private static async Task<BookDetailsResponse> ImportTextAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("第一章\n這是合法測試正文，只用來驗證語音工作流程。"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", $"narration-{Guid.NewGuid():N}.txt");
        using var response = await client.PostMultipartWithCsrfAsync("/api/books/import", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Import response did not contain a book.");
    }
}

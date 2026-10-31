using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryVoice.Application.Books;
using StoryVoice.Application.Insights;
using StoryVoice.Application.Library;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class LibraryStatusApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Matrix_reports_authorized_text_notes_and_narration_without_guessing_metadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var uploaded = await ImportTextAsync(client, cancellationToken);
        using var note = await client.PostWithCsrfAsync(
            $"/api/books/{uploaded.Id}/notes",
            new CreateReadingNoteRequest("使用者自己的狀態矩陣筆記。", null),
            cancellationToken);
        note.EnsureSuccessStatusCode();
        var narration = await SeedQueuedPublishedAsync(uploaded.Id, uploaded.Id, cancellationToken);

        using var syntheticResponse = await client.PostWithCsrfAsync(
            "/api/books/",
            new CreateBookRequest(
                "沒有上傳來源的書目",
                "測試作者",
                "zh-TW",
                "synthetic.txt",
                [new CreateChapterRequest(1, "章節", "這段文字缺少持久化上傳來源。")]),
            cancellationToken);
        syntheticResponse.EnsureSuccessStatusCode();
        var synthetic = await syntheticResponse.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken);
        Assert.NotNull(synthetic);

        using var response = await client.GetAsync("/api/library/status-matrix/", cancellationToken);
        response.EnsureSuccessStatusCode();
        var matrix = await response.Content.ReadFromJsonAsync<LibraryBookStatusResponse[]>(cancellationToken);

        Assert.NotNull(matrix);
        var uploadedStatus = Assert.Single(matrix, item => item.BookId == uploaded.Id);
        Assert.True(uploadedStatus.AuthorizedTextAvailable);
        Assert.Equal(1, uploadedStatus.ReadingNoteCount);
        Assert.Equal("Queued", uploadedStatus.StoryVoiceNarrationStatus);
        Assert.True(uploadedStatus.StoryVoiceNarrationMatchesAuthorizedText);
        Assert.Equal("processing", uploadedStatus.State);

        var blockedStatus = Assert.Single(matrix, item => item.BookId == synthetic.Id);
        Assert.False(blockedStatus.AuthorizedTextAvailable);
        Assert.Equal("blocked", blockedStatus.State);
        Assert.Equal("authorized_text_required", blockedStatus.BlockedReason);
    }

    [Fact]
    public async Task Matrix_prefers_active_current_content_but_keeps_old_audio_visible_after_unlink()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var firstContent = await ImportTextAsync(client, cancellationToken);
        var secondContent = await ImportTextAsync(client, cancellationToken);
        Guid targetId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var ownerId = await db.Books
                .Where(book => book.Id == firstContent.Id)
                .Select(book => book.OwnerId!.Value)
                .SingleAsync(cancellationToken);
            var target = Book.CreateExternal(
                ownerId,
                "狀態矩陣連結目標",
                "測試作者",
                "zh-TW",
                "test-provider",
                $"matrix-{Guid.NewGuid():N}",
                $"https://example.test/reader/{Guid.NewGuid():N}",
                null);
            db.Books.Add(target);
            await db.SaveChangesAsync(cancellationToken);
            targetId = target.Id;
        }

        using var firstLink = await PutWithCsrfAsync(
            client,
            $"/api/books/{targetId}/content-link",
            new SetBookContentLinkRequest(firstContent.Id),
            cancellationToken);
        Assert.True(firstLink.IsSuccessStatusCode, await firstLink.Content.ReadAsStringAsync(cancellationToken));
        var olderJob = await SeedQueuedPublishedAsync(targetId, firstContent.Id, cancellationToken);
        using var firstCancel = await client.PostWithCsrfAsync(
            $"/api/narrations/{olderJob.Id}/cancel",
            new { },
            cancellationToken);
        firstCancel.EnsureSuccessStatusCode();

        using var secondLink = await PutWithCsrfAsync(
            client,
            $"/api/books/{targetId}/content-link",
            new SetBookContentLinkRequest(secondContent.Id),
            cancellationToken);
        secondLink.EnsureSuccessStatusCode();
        await SeedQueuedPublishedAsync(targetId, secondContent.Id, cancellationToken);

        using var relink = await PutWithCsrfAsync(
            client,
            $"/api/books/{targetId}/content-link",
            new SetBookContentLinkRequest(firstContent.Id),
            cancellationToken);
        relink.EnsureSuccessStatusCode();
        await using (var requeueScope = factory.Services.CreateAsyncScope())
        {
            var requeueDb = requeueScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var requeued = await requeueDb.NarrationJobs.SingleAsync(item => item.Id == olderJob.Id, cancellationToken);
            requeued.Requeue(DateTimeOffset.UtcNow);
            await requeueDb.SaveChangesAsync(cancellationToken);
        }

        var currentMatrix = await client.GetFromJsonAsync<LibraryBookStatusResponse[]>(
            "/api/library/status-matrix/",
            cancellationToken);
        var currentStatus = Assert.Single(currentMatrix!, item => item.BookId == targetId);
        Assert.Equal("Queued", currentStatus.StoryVoiceNarrationStatus);
        Assert.True(currentStatus.StoryVoiceNarrationMatchesAuthorizedText);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var job = await db.NarrationJobs.SingleAsync(item => item.Id == olderJob.Id, cancellationToken);
            job.Claim("matrix-worker", DateTimeOffset.UtcNow.AddMinutes(20));
            job.Complete("matrix/old-content.mp3", 42);
            var audioPath = Path.Combine(factory.StorageRoot, "audio", "matrix", "old-content.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
            await File.WriteAllBytesAsync(audioPath, new byte[42], cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        using var unlink = await DeleteWithCsrfAsync(
            client,
            $"/api/books/{targetId}/content-link",
            cancellationToken);
        unlink.EnsureSuccessStatusCode();

        var unlinkedMatrix = await client.GetFromJsonAsync<LibraryBookStatusResponse[]>(
            "/api/library/status-matrix/",
            cancellationToken);
        var unlinkedStatus = Assert.Single(unlinkedMatrix!, item => item.BookId == targetId);
        Assert.False(unlinkedStatus.AuthorizedTextAvailable);
        Assert.Equal("Completed", unlinkedStatus.StoryVoiceNarrationStatus);
        Assert.False(unlinkedStatus.StoryVoiceNarrationMatchesAuthorizedText);
        Assert.Equal("audio_ready", unlinkedStatus.State);
        Assert.Equal("authorized_text_required", unlinkedStatus.BlockedReason);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(41, false)]
    [InlineData(43, false)]
    [InlineData(42, true)]
    public async Task Matrix_only_reports_completed_audio_ready_when_stored_file_matches(
        int actualBytes, bool expectedReady)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var book = await ImportTextAsync(client, cancellationToken);
        var queued = await SeedQueuedPublishedAsync(book.Id, book.Id, cancellationToken);
        var relativePath = $"matrix/{queued.Id:N}.mp3";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var job = await db.NarrationJobs.SingleAsync(item => item.Id == queued.Id, cancellationToken);
            job.Claim("matrix-worker", DateTimeOffset.UtcNow.AddMinutes(20));
            job.Complete(relativePath, 42);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (actualBytes >= 0)
        {
            var path = Path.Combine(factory.StorageRoot, "audio", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, new byte[actualBytes], cancellationToken);
        }

        var matrix = await client.GetFromJsonAsync<LibraryBookStatusResponse[]>(
            "/api/library/status-matrix/", cancellationToken);
        var item = Assert.Single(matrix!, candidate => candidate.BookId == book.Id);
        Assert.Equal("Completed", item.StoryVoiceNarrationStatus);
        Assert.Equal(expectedReady ? "audio_ready" : "failed", item.State);
        Assert.Equal(expectedReady ? null : "narration_audio_unavailable", item.BlockedReason);
    }

    [Fact]
    public async Task Matrix_ignores_staged_and_historical_artifacts_for_current_library_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        var currentBook = await ImportTextAsync(client, cancellationToken);
        var hiddenOnlyBook = await ImportTextAsync(client, cancellationToken);
        var published = await SeedQueuedPublishedAsync(currentBook.Id, currentBook.Id, cancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var ownerId = await db.Books
                .Where(book => book.Id == currentBook.Id)
                .Select(book => book.OwnerId!.Value)
                .SingleAsync(cancellationToken);
            db.NarrationJobs.AddRange(
                NarrationArtifactTestData.CompletedStaged(ownerId, currentBook.Id, currentBook.Id),
                NarrationArtifactTestData.CompletedHistorical(ownerId, currentBook.Id, currentBook.Id),
                NarrationArtifactTestData.CompletedStaged(ownerId, hiddenOnlyBook.Id, hiddenOnlyBook.Id),
                NarrationArtifactTestData.CompletedHistorical(ownerId, hiddenOnlyBook.Id, hiddenOnlyBook.Id));
            await db.SaveChangesAsync(cancellationToken);
        }

        var matrix = await client.GetFromJsonAsync<LibraryBookStatusResponse[]>(
            "/api/library/status-matrix/",
            cancellationToken);
        Assert.NotNull(matrix);

        var current = Assert.Single(matrix, item => item.BookId == currentBook.Id);
        Assert.Equal("Queued", current.StoryVoiceNarrationStatus);
        Assert.True(current.StoryVoiceNarrationMatchesAuthorizedText);
        Assert.Equal("processing", current.State);

        var hiddenOnly = Assert.Single(matrix, item => item.BookId == hiddenOnlyBook.Id);
        Assert.Null(hiddenOnly.StoryVoiceNarrationStatus);
        Assert.False(hiddenOnly.StoryVoiceNarrationMatchesAuthorizedText);
        Assert.Equal("ready", hiddenOnly.State);
        Assert.Null(hiddenOnly.BlockedReason);
    }

    [Fact]
    public async Task Matrix_is_owner_scoped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await factory.CreateAuthenticatedClientAsync(cancellationToken);
        _ = await ImportTextAsync(owner, cancellationToken);
        using var other = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        var matrix = await other.GetFromJsonAsync<LibraryBookStatusResponse[]>(
            "/api/library/status-matrix/",
            cancellationToken);

        Assert.NotNull(matrix);
        Assert.Empty(matrix);
    }

    private async Task<NarrationJob> SeedQueuedPublishedAsync(
        Guid bookId,
        Guid contentBookId,
        CancellationToken cancellationToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var ownerId = await db.Books
            .Where(book => book.Id == bookId)
            .Select(book => book.OwnerId!.Value)
            .SingleAsync(cancellationToken);
        var job = NarrationArtifactTestData.QueuedPublished(ownerId, bookId, contentBookId);
        db.NarrationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private static async Task<BookDetailsResponse> ImportTextAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("第一章\n自行撰寫的合法狀態矩陣測試正文。"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", $"matrix-{Guid.NewGuid():N}.txt");
        using var response = await client.PostMultipartWithCsrfAsync("/api/books/import", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken))!;
    }

    private static Task<HttpResponseMessage> PutWithCsrfAsync(
        HttpClient client,
        string requestUri,
        CancellationToken cancellationToken) =>
        client.SendWithCsrfAsync(
            new HttpRequestMessage(HttpMethod.Put, requestUri),
            cancellationToken);

    private static async Task<HttpResponseMessage> PutWithCsrfAsync<T>(
        HttpClient client,
        string requestUri,
        T body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = JsonContent.Create(body)
        };
        return await client.SendWithCsrfAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> DeleteWithCsrfAsync(
        HttpClient client,
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        return await client.SendWithCsrfAsync(request, cancellationToken);
    }
}

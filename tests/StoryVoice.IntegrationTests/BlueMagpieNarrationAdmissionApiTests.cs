using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Books;
using StoryVoice.Application.Narrations;
using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed partial class BlueMagpieNarrationAdmissionApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Retry_preserves_job_cast_and_plan_ids_and_remains_owner_scoped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var enabledFactory = CreateEnabledFactory();
        using var owner = await enabledFactory.CreateAuthenticatedClientAsync(ct);
        using var other = await enabledFactory.CreateAuthenticatedClientAsync(ct);
        var setup = await CreateBlueMagpieSeriesAsync(owner, "這是合成測試故事。", ct);
        var batch = await CreateFailedBatchAsync(enabledFactory, owner, setup.Series.Id, "provider_timeout", ct);
        var path = $"/api/series/{setup.Series.Id}/narration-rebuilds/{batch.Id}/retry";
        var before = await ReadPersistenceCountsAsync(enabledFactory.Services, setup.Series.Id, ct);
        var jobsBefore = await ReadJobsAsync(enabledFactory, batch.Id, ct);
        using var wrongOwner = await other.PostWithCsrfAsync(path, new { rightsAttested = true }, ct);
        using var noCsrf = await owner.PostAsJsonAsync(path, new { rightsAttested = true }, ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongOwner.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var otherList = await other.GetAsync($"/api/series/{setup.Series.Id}/narration-rebuilds", ct);
        Assert.Equal(HttpStatusCode.NotFound, otherList.StatusCode);
        var listed = await owner.GetFromJsonAsync<SeriesNarrationRebuildResponse[]>($"/api/series/{setup.Series.Id}/narration-rebuilds", ct);
        Assert.Equal(batch.Id, Assert.Single(listed!).Id);

        using var response = await owner.PostWithCsrfAsync(path, new { rightsAttested = true }, ct);
        response.EnsureSuccessStatusCode();
        var resumed = await response.Content.ReadFromJsonAsync<SeriesNarrationRebuildResponse>(ct);
        Assert.Equal(batch.Id, resumed!.Id);
        Assert.Equal(batch.DraftCastRevisionId, resumed.DraftCastRevisionId);
        Assert.Equal("Building", resumed.Status);
        Assert.Equal(before, await ReadPersistenceCountsAsync(enabledFactory.Services, setup.Series.Id, ct));
        var job = Assert.Single(await ReadJobsAsync(enabledFactory, batch.Id, ct));
        Assert.Equal(Assert.Single(jobsBefore).Id, job.Id);
        Assert.Equal(jobsBefore[0].SourceHash, job.SourceHash);
        Assert.Equal(jobsBefore[0].SpeechPlanRevisionId, job.SpeechPlanRevisionId);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.ErrorCode);
        Assert.Equal(NarrationJobStatus.Queued, job.Status);
        using var repeated = await owner.PostWithCsrfAsync(path, new { rightsAttested = true }, ct);
        repeated.EnsureSuccessStatusCode();
        Assert.Equal(before, await ReadPersistenceCountsAsync(enabledFactory.Services, setup.Series.Id, ct));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("cast")]
    [InlineData("gate")]
    [InlineData("budget")]
    [InlineData("permanent")]
    [InlineData("rights")]
    [InlineData("plan")]
    public async Task Retry_rejects_changed_or_unsafe_inputs_without_resetting_the_failed_job(string condition)
    {
        var ct = TestContext.Current.CancellationToken;
        using var enabledFactory = CreateEnabledFactory();
        using var owner = await enabledFactory.CreateAuthenticatedClientAsync(ct);
        var setup = await CreateBlueMagpieSeriesAsync(owner, new string('甲', 130), ct);
        var batch = await CreateFailedBatchAsync(enabledFactory, owner, setup.Series.Id,
            condition == "permanent" ? "bluemagpie_provider_contract_invalid" : "provider_failed", ct);
        if (condition == "plan")
            await ConfirmBookAsync(owner, setup.Series.Id, setup.Book, ct);
        await using (var scope = enabledFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            if (condition == "source")
            {
                var chapter = await db.Chapters.SingleAsync(chapter => chapter.BookId == setup.Book.Id, ct);
                db.Entry(chapter).Property(chapter => chapter.OriginalText).CurrentValue = "修改後的合成測試正文。";
            }
            if (condition == "cast")
            {
                var character = await db.SeriesCharacters.SingleAsync(character => character.SeriesId == setup.Series.Id, ct);
                db.Entry(character).Property(character => character.Voice).CurrentValue = BlueMagpieOptions.FemaleVoice;
            }
            await db.SaveChangesAsync(ct);
        }
        var options = enabledFactory.Services.GetRequiredService<IOptions<BlueMagpieOptions>>().Value;
        if (condition == "gate") options.FormalNarrationEnabled = false;
        if (condition == "budget") options.MaximumChunksPerJob = 1;
        var before = await ReadPersistenceCountsAsync(enabledFactory.Services, setup.Series.Id, ct);
        using var response = await owner.PostWithCsrfAsync($"/api/series/{setup.Series.Id}/narration-rebuilds/{batch.Id}/retry",
            new { rightsAttested = condition != "rights" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await ReadPersistenceCountsAsync(enabledFactory.Services, setup.Series.Id, ct));
        var job = Assert.Single(await ReadJobsAsync(enabledFactory, batch.Id, ct));
        Assert.Equal(NarrationJobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task Retry_preserves_completed_audio_and_requeues_siblings_cancelled_by_the_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        using var enabledFactory = CreateEnabledFactory();
        using var owner = await enabledFactory.CreateAuthenticatedClientAsync(ct);
        var setup = await CreateBlueMagpieSeriesAsync(owner, "第一冊的測試故事。", ct);
        for (var i = 2; i <= 3; i++)
        {
            var book = await ImportTextAsync(owner, $"第{i}冊的測試故事。", ct);
            using var added = await owner.PostWithCsrfAsync($"/api/series/{setup.Series.Id}/books",
                new { bookId = book.Id, volumeLabel = $"第{i}冊", sortOrder = i }, ct);
            added.EnsureSuccessStatusCode();
            await ConfirmBookAsync(owner, setup.Series.Id, book, ct);
        }
        using var created = await owner.PostWithCsrfAsync($"/api/series/{setup.Series.Id}/narration-rebuilds",
            new { rightsAttested = true }, ct);
        created.EnsureSuccessStatusCode();
        var batch = (await created.Content.ReadFromJsonAsync<SeriesNarrationRebuildResponse>(ct))!;
        Guid completedId;
        await using (var scope = enabledFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var jobs = await db.NarrationJobs.Where(job => job.RebuildBatchId == batch.Id).OrderBy(job => job.Id).ToArrayAsync(ct);
            completedId = jobs[0].Id;
            jobs[0].Claim("recovery-test", DateTimeOffset.UtcNow.AddMinutes(1));
            jobs[0].Complete("synthetic-completed.mp3", 100);
            jobs[1].Claim("recovery-test", DateTimeOffset.UtcNow.AddMinutes(1));
            jobs[1].FailOrRequeue("worker_lease_expired", maxAttempts: 1);
            await db.SaveChangesAsync(ct);
            var progress = scope.ServiceProvider.GetRequiredService<IStagedNarrationBatchProgressService>();
            await progress.SynchronizeAsync(completedId, ct);
            await progress.SynchronizeAsync(jobs[1].Id, ct);
        }
        var before = await ReadJobsAsync(enabledFactory, batch.Id, ct);
        Assert.Single(before, job => job.Status == NarrationJobStatus.Cancelled);
        using var retried = await owner.PostWithCsrfAsync($"/api/series/{setup.Series.Id}/narration-rebuilds/{batch.Id}/retry",
            new { rightsAttested = true }, ct);
        retried.EnsureSuccessStatusCode();
        var resumed = (await retried.Content.ReadFromJsonAsync<SeriesNarrationRebuildResponse>(ct))!;
        var after = await ReadJobsAsync(enabledFactory, batch.Id, ct);
        Assert.Equal(before.Select(job => job.Id).Order(), after.Select(job => job.Id).Order());
        var completed = Assert.Single(after, job => job.Id == completedId);
        Assert.Equal(NarrationJobStatus.Completed, completed.Status);
        Assert.Equal("synthetic-completed.mp3", completed.AudioRelativePath);
        Assert.Equal(100, completed.AudioBytes);
        Assert.Equal(before.Single(job => job.Id == completedId).CompletedAt, completed.CompletedAt);
        Assert.All(after.Where(job => job.Id != completedId), job => Assert.Equal(NarrationJobStatus.Queued, job.Status));
        Assert.Single(resumed.Members, member => member.Status == "Ready");
        Assert.Equal(2, resumed.Members.Count(member => member.Status == "Building"));
        var series = (await owner.GetFromJsonAsync<StorySeriesDetailsResponse>($"/api/series/{setup.Series.Id}", ct))!;
        Assert.Null(series.ActiveCastRevisionId);
        Assert.All(series.Books, book => Assert.Null(book.ActiveNarrationJobId));
    }

    private static async Task ConfirmBookAsync(HttpClient owner, Guid seriesId, BookDetailsResponse book, CancellationToken ct)
    {
        using var built = await owner.PostWithCsrfAsync($"/api/series/{seriesId}/books/{book.Id}/chapters/{Assert.Single(book.Chapters).Id}/speech-plan", new { }, ct);
        built.EnsureSuccessStatusCode();
        var draft = (await built.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(ct))!;
        using var confirmed = await owner.PostWithCsrfAsync($"/api/series/{seriesId}/speech-plan-drafts/{draft.Id}/confirm", new { }, ct);
        confirmed.EnsureSuccessStatusCode();
    }

    private static async Task<NarrationJob[]> ReadJobsAsync(WebApplicationFactory<Program> factory, Guid batchId, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>().NarrationJobs.AsNoTracking()
            .Where(job => job.RebuildBatchId == batchId).ToArrayAsync(ct);
    }

    private static async Task<SeriesNarrationRebuildResponse> CreateFailedBatchAsync(
        WebApplicationFactory<Program> factory, HttpClient owner, Guid seriesId, string errorCode, CancellationToken ct)
    {
        using var created = await owner.PostWithCsrfAsync($"/api/series/{seriesId}/narration-rebuilds", new { rightsAttested = true }, ct);
        created.EnsureSuccessStatusCode();
        var batch = (await created.Content.ReadFromJsonAsync<SeriesNarrationRebuildResponse>(ct))!;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        var job = await db.NarrationJobs.SingleAsync(job => job.RebuildBatchId == batch.Id, ct);
        job.Claim("recovery-test", DateTimeOffset.UtcNow.AddMinutes(1));
        job.FailOrRequeue(errorCode, maxAttempts: 1);
        await db.SaveChangesAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IStagedNarrationBatchProgressService>().SynchronizeAsync(job.Id, ct);
        return batch;
    }

    [Fact]
    public async Task Oversized_chunk_estimate_rejects_retry_without_purging_failed_batch_or_creating_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var enabledFactory = CreateEnabledFactory();
        using var owner = await enabledFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var setup = await CreateBlueMagpieSeriesAsync(
            owner,
            new string('甲', 130),
            cancellationToken);

        using var firstResponse = await owner.PostWithCsrfAsync(
            $"/api/series/{setup.Series.Id}/narration-rebuilds",
            new { rightsAttested = true },
            cancellationToken);
        var firstBatch = await firstResponse.Content.ReadFromJsonAsync<SeriesNarrationRebuildResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.NotNull(firstBatch);

        await using (var failureScope = enabledFactory.Services.CreateAsyncScope())
        {
            var db = failureScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
            var stagedJob = await db.NarrationJobs.SingleAsync(
                job => job.RebuildBatchId == firstBatch!.Id,
                cancellationToken);
            var failedAt = DateTimeOffset.UtcNow;
            stagedJob.Claim("bluemagpie-budget-test", failedAt.AddMinutes(5), failedAt);
            stagedJob.FailPermanently("synthetic_budget_setup");
            await db.SaveChangesAsync(cancellationToken);

            var progress = failureScope.ServiceProvider
                .GetRequiredService<IStagedNarrationBatchProgressService>();
            await progress.SynchronizeAsync(stagedJob.Id, cancellationToken);
        }

        var before = await ReadPersistenceCountsAsync(
            enabledFactory.Services,
            setup.Series.Id,
            cancellationToken);
        enabledFactory.Services.GetRequiredService<IOptions<BlueMagpieOptions>>()
            .Value.MaximumChunksPerJob = 1;

        using var retryResponse = await owner.PostWithCsrfAsync(
            $"/api/series/{setup.Series.Id}/narration-rebuilds",
            new { rightsAttested = true },
            cancellationToken);
        var retryBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, retryResponse.StatusCode);
        Assert.Contains("預估分段數", retryBody, StringComparison.Ordinal);
        Assert.Equal(
            before,
            await ReadPersistenceCountsAsync(
                enabledFactory.Services,
                setup.Series.Id,
                cancellationToken));

        await using var verifyScope = enabledFactory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        Assert.Equal(
            SeriesCastRebuildBatchStatus.Failed,
            (await verifyDb.SeriesCastRebuildBatches.SingleAsync(
                batch => batch.Id == firstBatch!.Id,
                cancellationToken)).Status);
    }

    [Fact]
    public async Task Oversized_conservative_pcm_estimate_rejects_before_creating_cast_batch_or_job()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var enabledFactory = CreateEnabledFactory(builder =>
        {
            builder.UseSetting("BlueMagpie:MaximumChunksPerJob", "10000");
            builder.UseSetting("BlueMagpie:MaximumJobAudioBytes", "67108864");
        });
        using var owner = await enabledFactory.CreateAuthenticatedClientAsync(cancellationToken);
        var setup = await CreateBlueMagpieSeriesAsync(
            owner,
            new string('乙', 900),
            cancellationToken);
        var before = await ReadPersistenceCountsAsync(
            enabledFactory.Services,
            setup.Series.Id,
            cancellationToken);

        using var response = await owner.PostWithCsrfAsync(
            $"/api/series/{setup.Series.Id}/narration-rebuilds",
            new { rightsAttested = true },
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("預估 PCM 音訊量", body, StringComparison.Ordinal);
        Assert.Equal(
            before,
            await ReadPersistenceCountsAsync(
                enabledFactory.Services,
                setup.Series.Id,
                cancellationToken));
        Assert.Equal(new PersistenceCounts(0, 0, 0, 0), before);
    }

    private WebApplicationFactory<Program> CreateEnabledFactory(
        Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("BlueMagpie:Enabled", "true");
            builder.UseSetting("BlueMagpie:FormalNarrationEnabled", "true");
            builder.UseSetting("BlueMagpie:InternalToken", new string('t', 32));
            configure?.Invoke(builder);
        });

    private static async Task<(StorySeriesDetailsResponse Series, BookDetailsResponse Book)>
        CreateBlueMagpieSeriesAsync(
            HttpClient owner,
            string text,
            CancellationToken cancellationToken)
    {
        var book = await ImportTextAsync(owner, text, cancellationToken);
        using var createResponse = await owner.PostWithCsrfAsync(
            "/api/series",
            new
            {
                name = $"BlueMagpie budget {Guid.NewGuid():N}",
                narratorProvider = "bluemagpie",
                narratorVoice = BlueMagpieOptions.FemaleVoice,
                narratorRate = "+0%",
                narratorPitch = "+0Hz",
                narratorVolume = "+0%",
                defaultSpeakerPauseMs = 180,
            },
            cancellationToken);
        var series = await createResponse.Content.ReadFromJsonAsync<StorySeriesDetailsResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(series);

        using var addBookResponse = await owner.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books",
            new { bookId = book.Id, volumeLabel = "第一冊", sortOrder = 1 },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, addBookResponse.StatusCode);

        using var addCharacterResponse = await owner.PostWithCsrfAsync(
            $"/api/series/{series.Id}/characters",
            new
            {
                canonicalName = "測試角色",
                role = "Main",
                voiceProvider = "bluemagpie",
                voice = BlueMagpieOptions.MaleVoice,
                rate = "+0%",
                pitch = "+0Hz",
                volume = "+0%",
                notes = (string?)null,
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, addCharacterResponse.StatusCode);

        var chapter = Assert.Single(book.Chapters);
        using var buildResponse = await owner.PostWithCsrfAsync(
            $"/api/series/{series.Id}/books/{book.Id}/chapters/{chapter.Id}/speech-plan",
            new { },
            cancellationToken);
        var draft = await buildResponse.Content.ReadFromJsonAsync<ChapterSpeechPlanDraftResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, buildResponse.StatusCode);
        Assert.NotNull(draft);

        using var confirmResponse = await owner.PostWithCsrfAsync(
            $"/api/series/{series.Id}/speech-plan-drafts/{draft.Id}/confirm",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        return (series, book);
    }

    private static async Task<BookDetailsResponse> ImportTextAsync(
        HttpClient client,
        string text,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"第一章\n{text}"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", $"bluemagpie-budget-{Guid.NewGuid():N}.txt");
        using var response = await client.PostMultipartWithCsrfAsync(
            "/api/books/import",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BookDetailsResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Import response did not contain a book.");
    }

    private static async Task<PersistenceCounts> ReadPersistenceCountsAsync(
        IServiceProvider services,
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
        return new PersistenceCounts(
            await db.NarrationCastRevisions.CountAsync(
                revision => revision.SeriesId == seriesId,
                cancellationToken),
            await db.SeriesCastRebuildBatches.CountAsync(
                batch => batch.SeriesId == seriesId,
                cancellationToken),
            await db.NarrationJobs.CountAsync(
                job => job.SeriesId == seriesId,
                cancellationToken),
            await db.NarrationJobSpeechPlans.CountAsync(
                link => link.SeriesId == seriesId,
                cancellationToken));
    }

    private sealed record PersistenceCounts(
        int CastRevisions,
        int Batches,
        int Jobs,
        int PlanLinks);
}

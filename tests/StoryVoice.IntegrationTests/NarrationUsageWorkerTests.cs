using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure;
using StoryVoice.Infrastructure.Identity;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;
using StoryVoice.Worker;
using Testcontainers.PostgreSql;

namespace StoryVoice.IntegrationTests;

public sealed class NarrationUsageWorkerTests
{
    [Theory]
    [InlineData("retired", "Failed", "Failed")]
    [InlineData("source_changed", "Failed", "Failed")]
    [InlineData("usage_unavailable", "Failed", null)]
    public async Task Retired_single_voice_jobs_never_call_a_provider_even_if_usage_storage_is_unavailable(
        string scenario, string expectedJobStatus, string? expectedUsageOutcome)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(ct);
        var root = Path.Combine(Path.GetTempPath(), $"storyvoice-usage-worker-{Guid.NewGuid():N}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = postgres.GetConnectionString(),
            ["Narration:AudioRootPath"] = root,
            ["Narration:MaxAttempts"] = "1",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStoryVoiceInfrastructure(config);
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var provider = new ScenarioProvider(scenario);
        using var worker = new StoryPipelineWorker(scopeFactory, provider,
            new NarrationProviderDispatcher(new NarrationProviderRegistry([])),
            serviceProvider.GetRequiredService<IOptions<NarrationOptions>>(),
            Options.Create(new BlueMagpieOptions()), NullLogger<StoryPipelineWorker>.Instance);
        Guid jobId;
        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
                await db.Database.MigrateAsync(ct);
                var owner = Guid.NewGuid();
                db.Users.Add(new ApplicationUser { Id = owner, UserName = "synthetic-usage-worker", SecurityStamp = Guid.NewGuid().ToString() });
                var book = Book.Create(owner, "測試用量", "測試作者", "zh-TW", "synthetic.txt");
                book.SetStoragePath("synthetic-import.txt");
                var chapter = book.AddChapter(1, "第一章", "小雨😀走進教室。");
                var source = NarrationSource.Create([new NarrationChapterSource(chapter.Id, 1, chapter.Title, chapter.OriginalText)]);
                var job = NarrationJob.Create(owner, book.Id, book.Id,
                    scenario == "source_changed" ? "stale-source" : source.SourceHash, "synthetic-voice", "+0%", DateTimeOffset.UtcNow);
                jobId = job.Id;
                db.Books.Add(book);
                db.NarrationJobs.Add(job);
                await db.SaveChangesAsync(ct);
                if (scenario == "usage_unavailable")
                    await db.Database.ExecuteSqlRawAsync("DROP TABLE narration_attempt_usage", ct);
            }
            await worker.StartAsync(ct);
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
                var job = await db.NarrationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId, ct);
                if (job.Status is NarrationJobStatus.Completed or NarrationJobStatus.Failed or NarrationJobStatus.Cancelled)
                {
                    Assert.Equal(expectedJobStatus, job.Status.ToString());
                    Assert.Equal(1, job.Attempts);
                    if (scenario != "source_changed") Assert.Equal("character_voice_required", job.ErrorCode);
                    break;
                }
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(40), "Synthetic narration did not reach its expected outcome.");
                await Task.Delay(50, ct);
            }
            await worker.StopAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.Equal(0, provider.Calls);
            if (expectedUsageOutcome is not null)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
                var usage = await db.NarrationAttemptUsage.AsNoTracking().SingleAsync(item => item.NarrationJobId == jobId, ct);
                Assert.Equal(expectedUsageOutcome, usage.Outcome);
                Assert.NotNull(usage.FinishedAt);
                Assert.True(usage.ElapsedMs >= 0);
                Assert.Null(usage.AudioBytes);
                Assert.Null(usage.InputCharacters);
                Assert.Null(usage.Provider);
                Assert.Null(usage.SynthesisElapsedMs);
                Assert.Null(usage.CompletedChunks);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ScenarioProvider(string scenario) : INarrationProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task SynthesizeAsync(string text, string outputPath, string voice, string rate,
            Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback, CancellationToken ct)
        {
            Calls++;
            if (progressCallback is not null) await progressCallback(new(1, 3), ct);
            Entered.TrySetResult();
            if (scenario == "failure") throw new InvalidOperationException("synthetic-provider-failure");
            if (scenario == "timeout") throw new OperationCanceledException();
            if (scenario == "cancel") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            await File.WriteAllBytesAsync(outputPath, [73, 68, 51, 1, 2, 3], ct);
        }
    }
}

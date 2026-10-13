using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
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

public sealed class NarrationWorkerPostgreSqlTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Parallel_worker_keeps_separate_leases_and_artifacts_and_recovers_after_stop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(ct);
        var root = Path.Combine(Path.GetTempPath(), $"storyvoice-parallel-worker-{Guid.NewGuid():N}");
        using var enabled = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<StoryVoiceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<StoryVoiceDbContext>>();
            services.AddDbContext<StoryVoiceDbContext>(o => o.UseNpgsql(postgres.GetConnectionString()));
            services.PostConfigure<NarrationOptions>(o => { o.AudioRootPath = root; o.MaximumConcurrentJobs = 2; });
        }));
        var serviceProvider = enabled.Services;
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var options = serviceProvider.GetRequiredService<IOptions<NarrationOptions>>();
        var provider = new ControlledProvider();
        using var worker = CreateWorker(provider);
        var owner = Guid.Empty;

        StoryPipelineWorker CreateWorker(IMultiVoiceNarrationProvider voiceProvider) => new(
            scopeFactory, new RetiredSingleVoiceProvider(), new NarrationProviderDispatcher(new NarrationProviderRegistry([voiceProvider])),
            options, Options.Create(new BlueMagpieOptions()), NullLogger<StoryPipelineWorker>.Instance);

        async Task<NarrationJob[]> ReadJobsAsync()
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>().NarrationJobs
                .AsNoTracking().Where(job => job.OwnerId == owner).ToArrayAsync(ct);
        }

        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>().Database.MigrateAsync(ct);
            using var client = await enabled.CreateAuthenticatedClientAsync(ct);
            for (var index = 0; index < 3; index++)
            {
                var setup = await ManagedNarrationRecoveryApiTests.CreateManagedSeriesAsync(client, $"第 {index} 個合成測試故事。", ct);
                using var response = await client.PostWithCsrfAsync($"/api/series/{setup.Series.Id}/narration-rebuilds", new { rightsAttested = true }, ct);
                response.EnsureSuccessStatusCode();
            }
            await using (var scope = scopeFactory.CreateAsyncScope())
                owner = await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>().Users.Select(u => u.Id).SingleAsync(ct);
            var referenceRoot = serviceProvider.GetRequiredService<IOptions<CharacterVoiceStorageOptions>>().Value.RootPath;
            Directory.CreateDirectory(Path.Combine(referenceRoot, "seeded"));
            await File.WriteAllBytesAsync(Path.Combine(referenceRoot, "seeded/reference.wav"), [82, 73, 70, 70], ct);
            await worker.StartAsync(ct);
            var first = await provider.Entered.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
            _ = await provider.Entered.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
            var running = await ReadJobsAsync();
            Assert.Equal(2, running.Count(job => job.Status == NarrationJobStatus.Running));
            Assert.Single(running, job => job.Status == NarrationJobStatus.Queued);
            Assert.Equal(2, running.Where(job => job.Status == NarrationJobStatus.Running).Select(job => job.LeaseOwner).Distinct().Count());
            Assert.Equal(2, provider.Active);

            first.Release.TrySetResult();
            _ = await provider.Entered.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
            var refilled = await ReadJobsAsync();
            var completed = Assert.Single(refilled, job => job.Status == NarrationJobStatus.Completed);
            Assert.Equal(2, refilled.Count(job => job.Status == NarrationJobStatus.Running));
            Assert.True(File.Exists(Path.Combine(root, completed.AudioRelativePath!)));
            Assert.Equal(1, completed.Attempts);
            await worker.StopAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.Equal(0, provider.Active);

            // Managed clone jobs retain their lease on stop. Expire only those synthetic
            // leases, then prove a new worker can complete them without touching the first output.
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
                await db.NarrationJobs.Where(job => job.OwnerId == owner && job.Status == NarrationJobStatus.Running)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), ct);
            }
            using var restarted = CreateWorker(new ControlledProvider { CompleteImmediately = true });
            try
            {
                await restarted.StartAsync(ct);
                var deadline = Stopwatch.StartNew();
                NarrationJob[] finished;
                do
                {
                    finished = await ReadJobsAsync();
                    if (finished.All(job => job.Status == NarrationJobStatus.Completed)) break;
                    Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "Synthetic jobs did not finish after lease recovery.");
                    await Task.Delay(50, ct);
                } while (true);
                Assert.Equal(completed.AudioRelativePath, finished.Single(job => job.Id == completed.Id).AudioRelativePath);
                Assert.Equal(3, finished.Select(job => job.AudioRelativePath).Distinct().Count());
                Assert.All(finished, job =>
                {
                    Assert.Null(job.LeaseOwner);
                    Assert.Equal(job.Id == completed.Id ? 1 : 2, job.Attempts);
                    Assert.Equal(ControlledProvider.Audio, File.ReadAllBytes(Path.Combine(root, job.AudioRelativePath!)));
                });
            }
            finally
            {
                await restarted.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), ct);
            }

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
                var usages = await db.NarrationAttemptUsage.AsNoTracking().Where(item => item.OwnerId == owner).ToArrayAsync(ct);
                Assert.Equal(5, usages.Length);
                Assert.Equal(3, usages.Count(item => item.Outcome == "Completed"));
                Assert.Equal(2, usages.Count(item => item.Outcome == "WorkerStopped"));
                Assert.All(usages, item =>
                {
                    Assert.NotNull(item.FinishedAt);
                    Assert.True(item.ElapsedMs >= item.SynthesisElapsedMs);
                    Assert.True(item.SynthesisElapsedMs >= 0);
                    Assert.True(item.InputCharacters > 0);
                    Assert.Equal(1, item.CompletedChunks);
                    Assert.Equal(2, item.TotalChunks);
                    Assert.Equal("local-clone", item.Provider);
                    Assert.Equal(item.Outcome == "Completed" ? ControlledProvider.Audio.Length : (long?)null, item.AudioBytes);
                });
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ControlledProvider : IMultiVoiceNarrationProvider
    {
        public string ProviderName => "local-clone";
        public static readonly byte[] Audio = Encoding.ASCII.GetBytes("ID3-synthetic-worker-output");
        public Channel<Call> Entered { get; } = Channel.CreateUnbounded<Call>();
        public bool CompleteImmediately { get; init; }
        private int _active;
        public int Active => Volatile.Read(ref _active);
        public async Task<MultiVoiceSynthesisResult> SynthesizeAsync(MultiVoiceNarrationRequest request, string outputPath,
            Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback, CancellationToken ct)
        {
            Interlocked.Increment(ref _active);
            try
            {
                if (progressCallback is not null) await progressCallback(new(1, 2), ct);
                var call = new Call();
                await Entered.Writer.WriteAsync(call, ct);
                if (!CompleteImmediately) await call.Release.Task.WaitAsync(ct);
                await File.WriteAllBytesAsync(outputPath, Audio, ct);
                await File.WriteAllTextAsync(outputPath + ".integrity.json", System.Text.Json.JsonSerializer.Serialize(new LocalCloneIntegrityReceipt(request.CacheContext, request.Turns.Sum(t => t.Text.Length)) { Status = "complete", AudioBytes = Audio.Length, AudioSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Audio)).ToLowerInvariant() }), ct);
                return MultiVoiceSynthesisResult.None;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public sealed class Call
        {
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

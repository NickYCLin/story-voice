using System.Data.Common;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace StoryVoice.IntegrationTests;

public sealed partial class BlueMagpieNarrationAdmissionApiTests
{
    [Fact]
    public async Task PostgreSql_serializes_retries_and_ignores_a_failure_callback_captured_before_recovery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(ct);
        using var enabledFactory = CreateEnabledFactory(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<StoryVoiceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<StoryVoiceDbContext>>();
            services.AddDbContext<StoryVoiceDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()));
        }));
        await using (var scope = enabledFactory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>().Database.MigrateAsync(ct);
        }
        using var owner = await enabledFactory.CreateAuthenticatedClientAsync(ct);
        var setup = await CreateBlueMagpieSeriesAsync(owner, "這是恢復配音的合成測試故事。", ct);
        var batch = await CreateFailedBatchAsync(enabledFactory, owner, setup.Series.Id, "provider_timeout", ct);
        var original = Assert.Single(await ReadJobsAsync(enabledFactory, batch.Id, ct));
        var before = await ReadPersistenceCountsAsync(enabledFactory.Services, setup.Series.Id, ct);

        // Pause an actual terminal SELECT after PostgreSQL has returned its old failure.
        // Recovery commits before that callback obtains the batch lock.
        var barrier = new TerminalReadBarrier();
        var options = new DbContextOptionsBuilder<StoryVoiceDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).AddInterceptors(barrier).Options;
        await using var callbackDb = new StoryVoiceDbContext(options);
        var callback = new StagedNarrationBatchProgressService(callbackDb).SynchronizeAsync(original.Id, ct);
        try
        {
            await barrier.Captured.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var path = $"/api/series/{setup.Series.Id}/narration-rebuilds/{batch.Id}/retry";
            var responses = await Task.WhenAll(
                owner.PostWithCsrfAsync(path, new { rightsAttested = true }, ct),
                owner.PostWithCsrfAsync(path, new { rightsAttested = true }, ct));
            foreach (var response in responses)
            {
                using (response)
                {
                    response.EnsureSuccessStatusCode();
                    var resumed = await response.Content.ReadFromJsonAsync<SeriesNarrationRebuildResponse>(ct);
                    Assert.Equal(batch.Id, resumed!.Id);
                    Assert.Equal("Building", resumed.Status);
                }
            }
        }
        finally
        {
            barrier.Release.TrySetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }

        var job = Assert.Single(await ReadJobsAsync(enabledFactory, batch.Id, ct));
        Assert.Equal(original.Id, job.Id);
        Assert.Equal(NarrationJobStatus.Queued, job.Status);
        Assert.False(job.CancellationRequested);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(before, await ReadPersistenceCountsAsync(enabledFactory.Services, setup.Series.Id, ct));
        var latest = await owner.GetFromJsonAsync<SeriesNarrationRebuildResponse>(
            $"/api/series/{setup.Series.Id}/narration-rebuilds/{batch.Id}", ct);
        Assert.Equal("Building", latest!.Status);
        Assert.Equal("Building", Assert.Single(latest.Members).Status);
    }

    private sealed class TerminalReadBarrier : DbCommandInterceptor
    {
        private int _reads;
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM narration_jobs", StringComparison.Ordinal)
                && Interlocked.Increment(ref _reads) == 1)
            {
                Captured.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            }
            return result;
        }
    }
}

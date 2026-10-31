using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StoryVoice.Infrastructure;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class NarrationJobSchedulerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Only_claims_available_slots_and_refills_one_finished_slot(int concurrency)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var claims = new ConcurrentQueue<Job>();
        var entered = new ConcurrentQueue<Job>();
        var allSlotsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = 0;
        var claimsInFlight = 0;
        var maximumClaimsInFlight = 0;
        var runner = NarrationJobScheduler.RunAsync(concurrency, async ct =>
        {
            var active = Interlocked.Increment(ref claimsInFlight);
            maximumClaimsInFlight = Math.Max(maximumClaimsInFlight, active);
            await Task.Yield();
            var job = new Job();
            claims.Enqueue(job);
            Interlocked.Decrement(ref claimsInFlight);
            return job;
        }, async (job, ct) =>
        {
            entered.Enqueue(job);
            if (entered.Count == concurrency) allSlotsEntered.TrySetResult();
            if (entered.Count == concurrency + 1) replacementEntered.TrySetResult();
            try { await job.Release.Task.WaitAsync(ct); }
            finally { Interlocked.Increment(ref cleanup); }
        }, exception => Assert.Fail(exception.Message), stop.Token);
        try
        {
            await allSlotsEntered.Task.WaitAsync(stop.Token);
            Assert.Equal(concurrency, claims.Count);
            Assert.Equal(1, maximumClaimsInFlight);
            entered.First().Release.TrySetResult();
            await replacementEntered.Task.WaitAsync(stop.Token);
            Assert.Equal(concurrency + 1, claims.Count);
            Assert.Equal(1, cleanup);
        }
        finally
        {
            await stop.CancelAsync();
            await runner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.Equal(claims.Count, cleanup);
    }

    [Fact]
    public async Task A_failed_job_is_reported_and_does_not_prevent_other_jobs_from_finishing()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failures = new ConcurrentQueue<Exception>();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Job();
        var second = new Job();
        var queue = new Queue<Job>([first, second]);
        var cleanedUp = false;
        var runner = NarrationJobScheduler.RunAsync(2, _ => Task.FromResult(queue.TryDequeue(out var job) ? job : null),
            async (job, ct) =>
            {
                if (ReferenceEquals(job, first)) throw new IOException("synthetic job failure");
                secondEntered.TrySetResult();
                try { await second.Release.Task.WaitAsync(ct); }
                finally { cleanedUp = true; }
            }, failures.Enqueue, stop.Token);
        try
        {
            await secondEntered.Task.WaitAsync(stop.Token);
            Assert.IsType<IOException>(Assert.Single(failures));
        }
        finally
        {
            await stop.CancelAsync();
            await runner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.True(cleanedUp);
    }

    [Fact]
    public async Task A_claim_returned_during_shutdown_still_runs_lease_cleanup()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var claimEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimed = new TaskCompletionSource<Job?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = false;
        var runner = NarrationJobScheduler.RunAsync(2, async _ =>
        {
            claimEntered.TrySetResult();
            return await claimed.Task;
        }, (_, ct) =>
        {
            Assert.True(ct.IsCancellationRequested);
            processed = true;
            return Task.CompletedTask;
        }, exception => Assert.Fail(exception.Message), stop.Token);
        await claimEntered.Task.WaitAsync(stop.Token);
        await stop.CancelAsync();
        claimed.TrySetResult(new Job());
        await runner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(processed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Rejects_unbounded_or_empty_concurrency_before_claiming(int concurrency)
    {
        Assert.Equal(1, new NarrationOptions().MaximumConcurrentJobs);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NarrationJobScheduler.RunAsync<Job>(concurrency,
            _ => throw new InvalidOperationException("must not claim"), (_, _) => Task.CompletedTask,
            _ => { }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Invalid_concurrency_is_rejected_by_application_options(int concurrency)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=synthetic;Username=synthetic;Password=synthetic",
            ["Narration:MaximumConcurrentJobs"] = concurrency.ToString(),
        }).Build();
        var services = new ServiceCollection();
        services.AddStoryVoiceInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<NarrationOptions>>().Value);
    }

    [Fact]
    public async Task Shutdown_interrupts_claim_failure_backoff_and_waits_for_active_work()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failedClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = 0;
        var runner = NarrationJobScheduler.RunAsync(2, _ => ++claims == 1
            ? Task.FromResult<Job?>(new Job()) : throw new IOException("synthetic claim failure"), async (_, ct) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally
                {
                    cleanupEntered.TrySetResult();
                    await releaseCleanup.Task;
                }
            }, _ => failedClaim.TrySetResult(), stop.Token);
        try
        {
            await failedClaim.Task.WaitAsync(stop.Token);
            await stop.CancelAsync();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(runner.IsCompleted);
            Assert.Equal(2, claims);
        }
        finally
        {
            await stop.CancelAsync();
            releaseCleanup.TrySetResult();
            await runner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    private sealed class Job
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

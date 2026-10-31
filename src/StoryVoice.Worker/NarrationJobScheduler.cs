namespace StoryVoice.Worker;

/// <summary>Claims only when a processing slot is available, using one claim/recovery loop.</summary>
internal static class NarrationJobScheduler
{
    public static async Task RunAsync<TJob>(
        int maximumConcurrentJobs,
        Func<CancellationToken, Task<TJob?>> claim,
        Func<TJob, CancellationToken, Task> process,
        Action<Exception> reportFailure,
        CancellationToken stoppingToken) where TJob : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentJobs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentJobs, 4);
        var running = new HashSet<Task>();

        async Task ProcessAsync(TJob job)
        {
            try
            {
                await process(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                reportFailure(exception);
            }
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var finished in running.Where(task => task.IsCompleted).ToArray())
                {
                    await finished;
                    running.Remove(finished);
                }
                if (running.Count == maximumConcurrentJobs)
                {
                    await Task.WhenAny(running);
                    continue;
                }

                TJob? job;
                try
                {
                    // Serial claims also keep the worker's lease-recovery sweep serialized.
                    job = await claim(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    reportFailure(exception);
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }

                if (job is not null)
                {
                    // A claim may commit just as shutdown starts. Always hand it to processing
                    // with the cancelled token so the existing stop/lease recovery rules apply.
                    running.Add(ProcessAsync(job));
                }
                else
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var poll = Task.Delay(TimeSpan.FromSeconds(2), idle.Token);
                    await Task.WhenAny(running.Append(poll));
                    await idle.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            // BackgroundService.StopAsync must observe every active job's cleanup.
            await Task.WhenAll(running);
        }
    }
}

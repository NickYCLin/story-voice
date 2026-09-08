using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Worker;

/// <summary>Best-effort operational history; a storage failure must never replay paid synthesis.</summary>
internal sealed class NarrationUsageRecorder(IServiceScopeFactory scopeFactory, ILogger logger, Guid id)
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private Stopwatch? _synthesis;
    private string? _provider;
    private long? _inputCharacters;
    private int? _completedChunks;
    private int? _totalChunks;

    public string Outcome { get; set; } = "Unknown";
    public long? AudioBytes { get; set; }

    public Task StartAsync(Guid ownerId, Guid jobId, string leaseOwner) => WriteAsync(async (db, ct) =>
    {
        db.NarrationAttemptUsage.Add(NarrationAttemptUsage.Start(id, ownerId, jobId, leaseOwner));
        await db.SaveChangesAsync(ct);
    });

    public async Task BeginSynthesisAsync(string provider, IEnumerable<string> texts)
    {
        // Store a fixed engine identifier, never a configured URL or provider response.
        _provider = provider is "edge" or "bluemagpie" or "voai" or "3wa-voxcpm2" ? provider : "unknown";
        _inputCharacters = CountCharacters(texts);
        await WriteAsync((db, ct) => db.NarrationAttemptUsage.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Provider, _provider)
                .SetProperty(item => item.InputCharacters, _inputCharacters), ct));
        _synthesis = Stopwatch.StartNew();
    }

    internal static long CountCharacters(IEnumerable<string> texts)
    {
        long count = 0;
        foreach (var text in texts)
            foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    public void ReportProgress(NarrationSynthesisProgress progress)
    {
        if (progress.TotalChunks <= 0 || progress.CompletedChunks < 0 || progress.CompletedChunks > progress.TotalChunks)
            return;
        if (_completedChunks > progress.CompletedChunks) return;
        _completedChunks = progress.CompletedChunks;
        _totalChunks = progress.TotalChunks;
    }

    public void EndSynthesis() => _synthesis?.Stop();

    public Task FinishAsync()
    {
        _elapsed.Stop();
        EndSynthesis();
        var finishedAt = DateTimeOffset.UtcNow;
        var elapsedMs = _elapsed.ElapsedMilliseconds;
        long? synthesisElapsedMs = _synthesis?.ElapsedMilliseconds;
        return WriteAsync(async (db, ct) =>
        {
            if (Outcome is "TimedOut" or "LeaseLost")
            {
                var cancellationRequested = await db.NarrationAttemptUsage.Where(item => item.Id == id)
                    .AnyAsync(item => db.NarrationJobs.Any(job => job.Id == item.NarrationJobId && job.CancellationRequested), ct);
                if (cancellationRequested) Outcome = "Cancelled";
            }
            await db.NarrationAttemptUsage.Where(item => item.Id == id && item.FinishedAt == null)
                .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Provider, _provider)
                .SetProperty(item => item.InputCharacters, _inputCharacters)
                .SetProperty(item => item.CompletedChunks, _completedChunks)
                .SetProperty(item => item.TotalChunks, _totalChunks)
                .SetProperty(item => item.Outcome, Outcome)
                .SetProperty(item => item.AudioBytes, AudioBytes)
                .SetProperty(item => item.ElapsedMs, elapsedMs)
                .SetProperty(item => item.SynthesisElapsedMs, synthesisElapsedMs)
                .SetProperty(item => item.FinishedAt, finishedAt), ct);
        });
    }

    private async Task WriteAsync(Func<StoryVoiceDbContext, CancellationToken, Task> write)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var scope = scopeFactory.CreateAsyncScope();
            await write(scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>(), timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Narration usage write failed for attempt {AttemptId} ({ErrorType})",
                id, exception.GetType().Name);
        }
    }
}

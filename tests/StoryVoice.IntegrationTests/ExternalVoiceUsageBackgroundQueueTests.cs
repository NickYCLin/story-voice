using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.IntegrationTests;

public sealed class ExternalVoiceUsageBackgroundQueueTests
{
    [Fact]
    public void Full_queue_rejects_immediately_and_emits_an_observable_warning()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var logger = new RecordingLogger<ExternalVoiceUsageBackgroundQueue>();
        using var queue = new ExternalVoiceUsageBackgroundQueue(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExternalVoiceApiOptions { UsageLedgerQueueCapacity = 1 }),
            logger);

        Assert.True(queue.TryEnqueue(CreateUsage("queue-first")));
        Assert.False(queue.TryEnqueue(CreateUsage("queue-dropped")));
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("queue-dropped", StringComparison.Ordinal)
                && entry.Message.Contains("capacity 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Worker_uses_a_fresh_scope_per_record_and_continues_after_save_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databaseName = $"usage-background-queue-{Guid.NewGuid():N}";
        var saveInterceptor = new FailFirstSaveChangesInterceptor();
        var services = new ServiceCollection();
        services.AddSingleton(saveInterceptor);
        services.AddDbContext<StoryVoiceDbContext>((provider, options) => options
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(provider.GetRequiredService<FailFirstSaveChangesInterceptor>()));
        services.AddScoped<ICurrentUser>(_ => new FixedCurrentUser(Guid.NewGuid()));
        services.AddScoped<ExternalVoiceUsageService>();

        await using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        var logger = new RecordingLogger<ExternalVoiceUsageBackgroundQueue>();
        using var queue = new ExternalVoiceUsageBackgroundQueue(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExternalVoiceApiOptions { UsageLedgerQueueCapacity = 4 }),
            logger);
        await queue.StartAsync(cancellationToken);

        Assert.True(queue.TryEnqueue(CreateUsage("queue-fails")));
        Assert.True(queue.TryEnqueue(CreateUsage("queue-persists")));

        await WaitUntilAsync(async () =>
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>()
                .ExternalVoiceUsageRecords
                .AnyAsync(record => record.RequestId == "queue-persists", cancellationToken);
        }, cancellationToken);
        await queue.StopAsync(cancellationToken);

        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var requestIds = await scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>()
                .ExternalVoiceUsageRecords
                .Select(record => record.RequestId)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(["queue-persists"], requestIds);
        }

        Assert.Equal(2, saveInterceptor.SaveContexts.Count);
        Assert.NotSame(saveInterceptor.SaveContexts[0], saveInterceptor.SaveContexts[1]);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error
                && entry.Message.Contains("queue-fails", StringComparison.Ordinal)
                && entry.Message.Contains("processing will continue", StringComparison.Ordinal));
    }

    private static ExternalVoiceUsageWrite CreateUsage(string requestId) =>
        new(
            Guid.NewGuid(),
            "consumer_01",
            "credential_01",
            "project-01",
            ExternalVoiceAccessTiers.PrivateDevelopment,
            requestId,
            "private-synthetic-voice",
            DateTimeOffset.UtcNow,
            200,
            ExternalVoiceUsageOutcomes.Succeeded,
            25,
            8,
            64,
            1_250);

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the background usage record.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class FixedCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }

    private sealed class FailFirstSaveChangesInterceptor : SaveChangesInterceptor
    {
        private int _saveAttempts;

        public List<DbContext> SaveContexts { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveContexts.Add(Assert.IsAssignableFrom<DbContext>(eventData.Context));
            if (Interlocked.Increment(ref _saveAttempts) == 1)
            {
                throw new InvalidOperationException("Synthetic first-write failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}

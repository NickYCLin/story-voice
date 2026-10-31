using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Identity;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace StoryVoice.IntegrationTests;

public sealed class ListeningProgressPostgreSqlTests
{
    [Fact]
    public async Task Migration_enforces_positions_and_concurrent_devices_cannot_overwrite_each_other()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(ct);
        var options = new DbContextOptionsBuilder<StoryVoiceDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
        var owner = Guid.NewGuid();
        var book = Book.Create(owner, "續播測試", "測試作者", "zh-TW", "progress.txt");
        var job = NarrationJob.Create(owner, book.Id, book.Id, "test-source", "test-voice", "+0%", DateTimeOffset.UtcNow);
        job.Claim("test-worker", DateTimeOffset.UtcNow.AddMinutes(1));
        job.Complete("synthetic.mp3", 100);
        await using var setup = new StoryVoiceDbContext(options);
        await setup.Database.MigrateAsync(ct);
        setup.Users.Add(new ApplicationUser { Id = owner, UserName = "progress-test", SecurityStamp = Guid.NewGuid().ToString() });
        setup.Books.Add(book);
        setup.NarrationJobs.Add(job);
        await setup.SaveChangesAsync(ct);

        // Both requests reach SaveChanges with the same previously read version (including
        // the first insert). The database must choose one winner, never last-writer-wins.
        Guid? version = null;
        for (var round = 0; round < 2; round++)
        {
            var barrier = new ConcurrentSaveBarrier();
            var racingOptions = new DbContextOptionsBuilder<StoryVoiceDbContext>()
                .UseNpgsql(postgres.GetConnectionString()).AddInterceptors(barrier).Options;
            async Task<SaveListeningProgressResult?> Save(long position)
            {
                await using var db = new StoryVoiceDbContext(racingOptions);
                var service = new NarrationService(db, new CurrentUser(owner), Options.Create(new NarrationOptions()));
                return await service.SaveListeningProgressAsync(job.Id, new(position, 120_000, version), ct);
            }

            var results = await Task.WhenAll(Save(42_000), Save(80_000));
            var winner = Assert.Single(results, result => result is { Conflict: false })!;
            var loser = Assert.Single(results, result => result is { Conflict: true })!;
            Assert.Equal(winner.Progress.Version, loser.Progress.Version);
            Assert.Equal(winner.Progress.PositionMs, loser.Progress.PositionMs);
            Assert.Equal(winner.Progress.DurationMs, loser.Progress.DurationMs);
            Assert.InRange((winner.Progress.UpdatedAt!.Value - loser.Progress.UpdatedAt!.Value).Duration(), TimeSpan.Zero, TimeSpan.FromMicroseconds(1));
            version = winner.Progress.Version;
            Assert.Equal(1, await setup.ListeningProgress.CountAsync(ct));
        }

        var invalid = await Assert.ThrowsAsync<PostgresException>(() => setup.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE listening_progress SET \"PositionMs\" = -1 WHERE \"NarrationJobId\" = {job.Id}", ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        setup.NarrationJobs.Remove(job);
        await setup.SaveChangesAsync(ct);
        Assert.Equal(0, await setup.ListeningProgress.CountAsync(ct));
    }

    private sealed record CurrentUser(Guid UserId) : ICurrentUser;

    private sealed class ConcurrentSaveBarrier : SaveChangesInterceptor
    {
        private int _arrivals;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _ready.SetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }
}

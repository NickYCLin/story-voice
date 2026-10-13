using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Identity;
using StoryVoice.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace StoryVoice.IntegrationTests;

public sealed class PlaybackAndUsagePostgreSqlMigrationTests
{
    private const string Baseline = "20260826090725_AddExternalVoiceUsageLedger";
    private static readonly string[] Upgrades =
    ["20260902025936_AddNarrationTimeline", "20260908010141_AddListeningProgress", "20260908025724_AddNarrationAttemptUsage"];

    [Fact]
    public async Task Pending_migrations_preserve_existing_rows_enforce_constraints_and_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(ct);
        await using var db = new StoryVoiceDbContext(new DbContextOptionsBuilder<StoryVoiceDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Baseline, ct);
        var owners = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var jobs = new List<NarrationJob>();
        foreach (var owner in owners)
        {
            db.Users.Add(new ApplicationUser { Id = owner, UserName = $"fixture-{owner:N}", SecurityStamp = Guid.NewGuid().ToString("N") });
            var book = Book.Create(owner, "Migration fixture", "Fixture", "zh-TW", "fixture.txt");
            db.Books.Add(book);
            for (var i = 0; i < 3; i++)
            {
                var job = NarrationJob.Create(owner, book.Id, book.Id, $"fixture-{i}", "fixture", "0%", DateTimeOffset.UtcNow);
                if (i == 0) { job.Claim("fixture", DateTimeOffset.UtcNow.AddMinutes(5)); job.Complete($"{owner:N}/{job.Id:N}.mp3", 100); }
                if (i == 1) job.Cancel();
                jobs.Add(job);
                db.NarrationJobs.Add(job);
            }
        }
        await db.SaveChangesAsync(ct);
        var before = await DigestAsync(db, ct);
        foreach (var migration in Upgrades)
        {
            await migrator.MigrateAsync(migration, ct);
            Assert.Equal(before, await DigestAsync(db, ct));
        }
        await migrator.MigrateAsync(Upgrades[^1], ct);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
        var jobId = jobs[0].Id;
        var ownerId = owners[0];
        var otherOwnerId = owners[1];
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO listening_progress ("OwnerId", "NarrationJobId", "PositionMs", "DurationMs", "Version", "UpdatedAt")
            VALUES ({ownerId}, {jobId}, 0, 1000, {Guid.NewGuid()}, {now});
            """, ct);
        var invalidPosition = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE listening_progress SET "PositionMs" = 1001 WHERE "NarrationJobId" = {jobId};
            """, ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidPosition.SqlState);
        var wrongOwner = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO narration_attempt_usage ("Id", "OwnerId", "NarrationJobId", "LeaseOwner", "StartedAt", "Outcome")
            VALUES ({Guid.NewGuid()}, {otherOwnerId}, {jobId}, 'fixture', {now}, 'Running');
            """, ct));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongOwner.SqlState);
        var badQuantity = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO narration_attempt_usage ("Id", "OwnerId", "NarrationJobId", "LeaseOwner", "StartedAt", "Outcome", "InputCharacters")
            VALUES ({Guid.NewGuid()}, {ownerId}, {jobId}, 'fixture', {now}, 'Running', -1);
            """, ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badQuantity.SqlState);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO narration_timelines ("OwnerId", "NarrationJobId", "TimelineJson", "CreatedAt")
            VALUES ({ownerId}, {jobId}, CAST({"{}"} AS jsonb), {now});
            """, ct);
        await migrator.MigrateAsync(Baseline, ct);
        Assert.Equal(before, await DigestAsync(db, ct));
        Assert.Equal(3, (await db.Database.GetPendingMigrationsAsync(ct)).Count());
        await migrator.MigrateAsync(Upgrades[^1], ct);
        Assert.Equal(before, await DigestAsync(db, ct));
        // Down explicitly removes data stored only in the new tables; never promise it is retained.
        Assert.Equal(0, await db.ListeningProgress.CountAsync(ct));
        Assert.Equal(0, await db.NarrationTimelines.CountAsync(ct));
    }

    private static async Task<string> DigestAsync(StoryVoiceDbContext db, CancellationToken ct)
    {
        var rows = await db.NarrationJobs.AsNoTracking().OrderBy(job => job.Id)
            .Select(job => new { job.Id, job.OwnerId, job.BookId, job.SourceHash, job.Status, job.Visibility, job.AudioRelativePath, job.AudioBytes, job.ConcurrencyStamp })
            .ToListAsync(ct);
        return System.Text.Json.JsonSerializer.Serialize(rows);
    }
}

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Domain.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;
using StoryVoice.Infrastructure.Identity;
using StoryVoice.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace StoryVoice.IntegrationTests;

public sealed class ExternalVoiceDeveloperPersistencePostgreSqlTests
{
    private const string ConsumerKeyId = "persistence_project_01";
    private const string ProjectId = "persistence-project";

    [Fact]
    public async Task Usage_summary_uses_one_snapshot_query_and_keeps_activity_owner_scoped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(cancellationToken);
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var baseOptions = new DbContextOptionsBuilder<StoryVoiceDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;

        await using (var setup = new StoryVoiceDbContext(baseOptions))
        {
            await setup.Database.MigrateAsync(cancellationToken);
            setup.Users.AddRange(CreateUser(ownerId), CreateUser(otherOwnerId));
            setup.ExternalVoiceUsageRecords.AddRange(
                CreateUsage(ownerId, now.AddMinutes(-2), "usage-summary-01", "succeeded", 200, 100, 64),
                CreateUsage(ownerId, now.AddMinutes(-1), "usage-summary-02", "rate_limited", 429, 300, 0),
                CreateUsage(otherOwnerId, now.AddMinutes(-1), "usage-summary-03", "succeeded", 200, 900, 512));
            await setup.SaveChangesAsync(cancellationToken);
        }

        var commands = new UsageQueryCommandInterceptor(async operationToken =>
        {
            await using var concurrentWrite = new StoryVoiceDbContext(baseOptions);
            concurrentWrite.ExternalVoiceUsageRecords.Add(
                CreateUsage(
                    ownerId,
                    now,
                    "usage-summary-concurrent",
                    "succeeded",
                    200,
                    50,
                    32));
            await concurrentWrite.SaveChangesAsync(operationToken);
        });
        var queryOptions = new DbContextOptionsBuilder<StoryVoiceDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .AddInterceptors(commands)
            .Options;
        await using var queryDb = new StoryVoiceDbContext(queryOptions);
        var service = new ExternalVoiceUsageService(queryDb, new FixedCurrentUser(ownerId));

        var report = await service.GetUsageAsync(
            new DeveloperVoiceUsageQuery(
                now.AddHours(-1),
                now.AddHours(1),
                ProjectId: null,
                VoiceAlias: null,
                ActivityLimit: 20),
            cancellationToken);

        Assert.Equal(2, report.Summary.TotalRequests);
        Assert.Equal(1, report.Summary.SuccessfulRequests);
        Assert.Equal(1, report.Summary.RateLimitedRequests);
        Assert.Equal(200, report.Summary.AverageLatencyMilliseconds);
        Assert.Equal(64, report.Summary.OutputBytes);
        Assert.Equal(2, report.Activities.Count);
        Assert.DoesNotContain(
            report.Activities,
            activity => activity.RequestId == "usage-summary-concurrent");
        Assert.Equal(2, commands.UsageSelectCommands);
    }

    [Fact]
    public async Task Managed_credentials_keep_last_used_and_capacity_monotonic_across_replicas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(cancellationToken);
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var dbOptions = new DbContextOptionsBuilder<StoryVoiceDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        var seeded = Enumerable.Range(0, 4)
            .Select(index => ExternalVoiceCredential.Create(
                ownerId,
                ConsumerKeyId,
                $"seed_credential_{index:D2}",
                $"測試金鑰 {index + 1}",
                index.ToString("x").PadLeft(64, '0'),
                now.AddHours(-1),
                now.AddDays(20)))
            .ToArray();

        await using (var setup = new StoryVoiceDbContext(dbOptions))
        {
            await setup.Database.MigrateAsync(cancellationToken);
            setup.Users.Add(CreateUser(ownerId));
            setup.ExternalVoiceCredentials.AddRange(seeded);
            await setup.SaveChangesAsync(cancellationToken);
        }

        var laterUse = TruncateToPostgreSqlMicroseconds(now.AddMinutes(2));
        await RecordUseAsync(dbOptions, seeded[0].Id, laterUse, cancellationToken);
        await RecordUseAsync(
            dbOptions,
            seeded[0].Id,
            TruncateToPostgreSqlMicroseconds(now.AddMinutes(1)),
            cancellationToken);
        await using (var verification = new StoryVoiceDbContext(dbOptions))
        {
            Assert.Equal(
                laterUse,
                (await verification.ExternalVoiceCredentials.AsNoTracking().SingleAsync(
                    credential => credential.Id == seeded[0].Id,
                    cancellationToken)).LastUsedAtUtc);
        }

        var apiOptions = CreateApiOptions(ownerId, now);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creates = Enumerable.Range(0, 8)
            .Select(index => CreateCredentialAsync(
                dbOptions,
                apiOptions,
                ownerId,
                $"並行金鑰 {index + 1}",
                start.Task,
                cancellationToken))
            .ToArray();
        start.SetResult();
        var issued = await Task.WhenAll(creates);
        Assert.Single(issued, credential => credential is not null);
        Assert.Equal(5, await CountActiveAsync(
            dbOptions,
            ownerId,
            DateTimeOffset.UtcNow,
            cancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RotateAsync(
            dbOptions,
            apiOptions,
            ownerId,
            seeded[0].Id,
            overlapMinutes: 60,
            cancellationToken));
        Assert.Equal(5, await CountActiveAsync(
            dbOptions,
            ownerId,
            DateTimeOffset.UtcNow,
            cancellationToken));

        var immediate = await RotateAsync(
            dbOptions,
            apiOptions,
            ownerId,
            seeded[0].Id,
            overlapMinutes: 0,
            cancellationToken);
        Assert.NotNull(immediate);
        Assert.Equal(5, await CountActiveAsync(
            dbOptions,
            ownerId,
            DateTimeOffset.UtcNow,
            cancellationToken));

        var rotateStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rotations = Enumerable.Range(0, 6)
            .Select(_ => RotateCredentialAsync(
                dbOptions,
                apiOptions,
                ownerId,
                immediate.Credential.Id!.Value,
                rotateStart.Task,
                cancellationToken))
            .ToArray();
        rotateStart.SetResult();
        var rotated = await Task.WhenAll(rotations);
        Assert.Single(rotated, credential => credential is not null);
        Assert.Equal(5, await CountActiveAsync(
            dbOptions,
            ownerId,
            DateTimeOffset.UtcNow,
            cancellationToken));
    }

    private static async Task RecordUseAsync(
        DbContextOptions<StoryVoiceDbContext> dbOptions,
        Guid credentialId,
        DateTimeOffset usedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new StoryVoiceDbContext(dbOptions);
        var credential = await dbContext.ExternalVoiceCredentials.SingleAsync(
            candidate => candidate.Id == credentialId,
            cancellationToken);
        await new ExternalVoiceCredentialUsageUpdater(dbContext).RecordUseAsync(
            credential,
            usedAtUtc,
            cancellationToken);
    }

    private static async Task<IssuedDeveloperVoiceCredential?> CreateCredentialAsync(
        DbContextOptions<StoryVoiceDbContext> dbOptions,
        ExternalVoiceApiOptions apiOptions,
        Guid ownerId,
        string name,
        Task start,
        CancellationToken cancellationToken)
    {
        await start;
        await using var dbContext = new StoryVoiceDbContext(dbOptions);
        var service = CreateCredentialService(dbContext, apiOptions, ownerId);
        try
        {
            return await service.CreateAsync(
                new CreateDeveloperVoiceCredentialRequest(ProjectId, name),
                cancellationToken);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("最多只能有", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private static async Task<IssuedDeveloperVoiceCredential?> RotateCredentialAsync(
        DbContextOptions<StoryVoiceDbContext> dbOptions,
        ExternalVoiceApiOptions apiOptions,
        Guid ownerId,
        Guid credentialId,
        Task start,
        CancellationToken cancellationToken)
    {
        await start;
        try
        {
            return await RotateAsync(
                dbOptions,
                apiOptions,
                ownerId,
                credentialId,
                overlapMinutes: 0,
                cancellationToken);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("只有仍有效", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private static async Task<IssuedDeveloperVoiceCredential?> RotateAsync(
        DbContextOptions<StoryVoiceDbContext> dbOptions,
        ExternalVoiceApiOptions apiOptions,
        Guid ownerId,
        Guid credentialId,
        int overlapMinutes,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new StoryVoiceDbContext(dbOptions);
        return await CreateCredentialService(dbContext, apiOptions, ownerId).RotateAsync(
            credentialId,
            new RotateDeveloperVoiceCredentialRequest(overlapMinutes),
            cancellationToken);
    }

    private static DeveloperVoiceCredentialService CreateCredentialService(
        StoryVoiceDbContext dbContext,
        ExternalVoiceApiOptions apiOptions,
        Guid ownerId) =>
        new(
            dbContext,
            Options.Create(apiOptions),
            new FixedCurrentUser(ownerId),
            TimeProvider.System,
            // Separate coordinators simulate independent API replicas. PostgreSQL's owner
            // row lock, not an in-process semaphore, must preserve the invariant.
            new DeveloperVoiceCredentialMutationCoordinator());

    private static async Task<int> CountActiveAsync(
        DbContextOptions<StoryVoiceDbContext> dbOptions,
        Guid ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new StoryVoiceDbContext(dbOptions);
        return await dbContext.ExternalVoiceCredentials.CountAsync(
            credential => credential.OwnerId == ownerId
                && credential.ConsumerKeyId == ConsumerKeyId
                && credential.ExpiresAtUtc > now
                && (credential.RevokedAtUtc == null || credential.RevokedAtUtc > now),
            cancellationToken);
    }

    private static ExternalVoiceApiOptions CreateApiOptions(Guid ownerId, DateTimeOffset now) =>
        new()
        {
            Enabled = true,
            Consumers = new Dictionary<string, ExternalVoiceConsumerOptions>(StringComparer.Ordinal)
            {
                [ConsumerKeyId] = new ExternalVoiceConsumerOptions
                {
                    AccessTier = ExternalVoiceAccessTiers.PrivateDevelopment,
                    DisplayName = "Persistence test project",
                    ProjectId = ProjectId,
                    OwnerId = ownerId,
                    TokenSha256 = new string('f', 64),
                    EffectiveAtUtc = now.AddHours(-1),
                    ExpiresAtUtc = now.AddDays(20),
                },
            },
        };

    private static ExternalVoiceUsageRecord CreateUsage(
        Guid ownerId,
        DateTimeOffset occurredAtUtc,
        string requestId,
        string outcome,
        int statusCode,
        int durationMilliseconds,
        long responseBytes) =>
        ExternalVoiceUsageRecord.Create(
            ownerId,
            ConsumerKeyId,
            ConsumerKeyId,
            ProjectId,
            ExternalVoiceAccessTiers.PrivateDevelopment,
            requestId,
            "private-synthetic-voice",
            occurredAtUtc,
            statusCode,
            outcome,
            durationMilliseconds,
            textCharacters: 8,
            responseBytes,
            audioDurationMilliseconds: responseBytes > 0 ? 1_000 : 0);

    private static ApplicationUser CreateUser(Guid ownerId) =>
        new()
        {
            Id = ownerId,
            UserName = $"persistence-{ownerId:N}@example.com",
            NormalizedUserName = $"PERSISTENCE-{ownerId:N}@EXAMPLE.COM",
            Email = $"persistence-{ownerId:N}@example.com",
            NormalizedEmail = $"PERSISTENCE-{ownerId:N}@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };

    private static DateTimeOffset TruncateToPostgreSqlMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), TimeSpan.Zero);

    private sealed class FixedCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }

    private sealed class UsageQueryCommandInterceptor(
        Func<CancellationToken, Task>? beforeSecondUsageSelect = null) : DbCommandInterceptor
    {
        public int UsageSelectCommands { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "external_voice_usage_records",
                    StringComparison.Ordinal))
            {
                UsageSelectCommands++;
                if (UsageSelectCommands == 2 && beforeSecondUsageSelect is not null)
                {
                    await beforeSecondUsageSelect(cancellationToken);
                }
            }

            return result;
        }
    }
}

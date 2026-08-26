using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using StoryVoice.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace StoryVoice.IntegrationTests;

public sealed class ExternalVoiceUsageLedgerPostgreSqlMigrationTests
{
    private const string PreviousMigration = "20260826081822_RemoveBookshelfCompanion";
    private const string CurrentMigration = "20260826090725_AddExternalVoiceUsageLedger";

    [Fact]
    public async Task Usage_migration_adds_and_removes_only_the_ledger_table()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(cancellationToken);
        var connectionString = postgres.GetConnectionString();

        await using var db = new StoryVoiceDbContext(
            new DbContextOptionsBuilder<StoryVoiceDbContext>()
                .UseNpgsql(connectionString)
                .Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, cancellationToken);
        Assert.False(await TableExistsAsync(connectionString, "external_voice_usage_records", cancellationToken));
        Assert.True(await TableExistsAsync(connectionString, "books", cancellationToken));

        await migrator.MigrateAsync(CurrentMigration, cancellationToken);
        Assert.True(await TableExistsAsync(connectionString, "external_voice_usage_records", cancellationToken));
        Assert.True(await TableExistsAsync(connectionString, "books", cancellationToken));

        await migrator.MigrateAsync(PreviousMigration, cancellationToken);
        Assert.False(await TableExistsAsync(connectionString, "external_voice_usage_records", cancellationToken));
        Assert.True(await TableExistsAsync(connectionString, "books", cancellationToken));
    }

    private static async Task<bool> TableExistsAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('public.' || @tableName) IS NOT NULL",
            connection);
        command.Parameters.AddWithValue("tableName", tableName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

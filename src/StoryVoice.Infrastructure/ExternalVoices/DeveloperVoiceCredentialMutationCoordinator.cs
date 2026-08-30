using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Infrastructure.ExternalVoices;

public sealed class DeveloperVoiceCredentialMutationCoordinator
{
    private const int ProcessLockStripes = 64;

    private readonly SemaphoreSlim[] processLocks = Enumerable.Range(0, ProcessLockStripes)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public async Task<TResult> ExecuteAsync<TResult>(
        StoryVoiceDbContext dbContext,
        Guid ownerId,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(operation);
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("Credential owner is required.", nameof(ownerId));
        }

        var processLock = processLocks[(ownerId.GetHashCode() & int.MaxValue) % ProcessLockStripes];
        await processLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(
                    dbContext.Database.ProviderName,
                    "Npgsql.EntityFrameworkCore.PostgreSQL",
                    StringComparison.Ordinal))
            {
                return await operation(cancellationToken);
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
            await LockOwnerRowAsync(dbContext, ownerId, cancellationToken);
            var result = await operation(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            processLock.Release();
        }
    }

    private static async Task LockOwnerRowAsync(
        StoryVoiceDbContext dbContext,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Credential mutation transaction is unavailable.");
        command.CommandText = "SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = @ownerId FOR UPDATE";
        var ownerParameter = command.CreateParameter();
        ownerParameter.ParameterName = "ownerId";
        ownerParameter.Value = ownerId;
        command.Parameters.Add(ownerParameter);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException("Credential owner no longer exists.");
        }
    }
}

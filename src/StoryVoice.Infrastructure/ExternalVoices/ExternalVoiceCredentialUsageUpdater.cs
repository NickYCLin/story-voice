using Microsoft.EntityFrameworkCore;
using StoryVoice.Domain.ExternalVoices;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Infrastructure.ExternalVoices;

public sealed class ExternalVoiceCredentialUsageUpdater(StoryVoiceDbContext dbContext)
{
    public async Task RecordUseAsync(
        ExternalVoiceCredential credential,
        DateTimeOffset usedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (dbContext.Database.IsRelational())
        {
            // A conditional database update is monotonic even when an older request saves
            // after a newer request in another DbContext or API replica.
            await dbContext.ExternalVoiceCredentials
                .Where(candidate => candidate.Id == credential.Id
                    && (candidate.LastUsedAtUtc == null
                        || candidate.LastUsedAtUtc < usedAtUtc))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        candidate => candidate.LastUsedAtUtc,
                        usedAtUtc),
                    cancellationToken);
            return;
        }

        // The EF in-memory provider used by API tests does not implement ExecuteUpdate.
        credential.RecordUse(usedAtUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

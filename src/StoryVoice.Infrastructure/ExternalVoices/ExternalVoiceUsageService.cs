using Microsoft.EntityFrameworkCore;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Domain.ExternalVoices;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Infrastructure.ExternalVoices;

public sealed class ExternalVoiceUsageService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser) : IExternalVoiceUsageRecorder, IDeveloperVoiceUsageService
{
    public async Task RecordAsync(
        ExternalVoiceUsageWrite usage,
        CancellationToken cancellationToken)
    {
        dbContext.ExternalVoiceUsageRecords.Add(ExternalVoiceUsageRecord.Create(
            usage.OwnerId,
            usage.ConsumerKeyId,
            usage.CredentialKeyId,
            usage.ProjectId,
            usage.AccessTier,
            usage.RequestId,
            usage.VoiceAlias,
            usage.OccurredAtUtc,
            usage.StatusCode,
            usage.Outcome,
            usage.DurationMilliseconds,
            usage.TextCharacters,
            usage.ResponseBytes,
            usage.AudioDurationMilliseconds));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeveloperVoiceUsageReport> GetUsageAsync(
        DeveloperVoiceUsageQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);

        var ownerId = currentUser.UserId;
        var records = dbContext.ExternalVoiceUsageRecords
            .AsNoTracking()
            .Where(record =>
                record.OwnerId == ownerId
                && record.OccurredAtUtc >= query.FromUtc
                && record.OccurredAtUtc < query.ToUtc);

        if (!string.IsNullOrEmpty(query.ProjectId))
        {
            records = records.Where(record => record.ProjectId == query.ProjectId);
        }

        if (!string.IsNullOrEmpty(query.VoiceAlias))
        {
            records = records.Where(record => record.VoiceAlias == query.VoiceAlias);
        }

        var totalRequests = await records.CountAsync(cancellationToken);
        var successfulRequests = await records.CountAsync(
            record => record.Outcome == ExternalVoiceUsageOutcomes.Succeeded,
            cancellationToken);
        var rateLimitedRequests = await records.CountAsync(
            record => record.Outcome == "rate_limited",
            cancellationToken);
        var averageLatency = await records
            .Select(record => (double?)record.DurationMilliseconds)
            .AverageAsync(cancellationToken) ?? 0;
        var outputBytes = await records
            .Select(record => (long?)record.ResponseBytes)
            .SumAsync(cancellationToken) ?? 0;
        var outputDuration = await records
            .Select(record => (long?)record.AudioDurationMilliseconds)
            .SumAsync(cancellationToken) ?? 0;
        var activities = await records
            .OrderByDescending(record => record.OccurredAtUtc)
            .ThenByDescending(record => record.Id)
            .Take(query.ActivityLimit)
            .Select(record => new DeveloperVoiceUsageActivity(
                record.RequestId,
                record.ProjectId,
                record.VoiceAlias,
                record.OccurredAtUtc,
                record.StatusCode,
                record.Outcome,
                record.DurationMilliseconds,
                record.TextCharacters,
                record.ResponseBytes,
                record.AudioDurationMilliseconds))
            .ToArrayAsync(cancellationToken);

        return new DeveloperVoiceUsageReport(
            query.FromUtc,
            query.ToUtc,
            new DeveloperVoiceUsageSummary(
                totalRequests,
                successfulRequests,
                totalRequests == 0
                    ? 0
                    : Math.Round(successfulRequests * 100d / totalRequests, 1),
                rateLimitedRequests,
                Math.Round(averageLatency, 1),
                outputBytes,
                outputDuration),
            activities);
    }

    private static void ValidateQuery(DeveloperVoiceUsageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.FromUtc == default
            || query.ToUtc == default
            || query.FromUtc.Offset != TimeSpan.Zero
            || query.ToUtc.Offset != TimeSpan.Zero
            || query.FromUtc >= query.ToUtc
            || query.ToUtc - query.FromUtc > TimeSpan.FromDays(90))
        {
            throw new ArgumentException("Usage window must be a UTC range of at most 90 days.");
        }

        if (query.ActivityLimit is < 1 or > 100
            || !IsOptionalIdentifier(query.ProjectId, 128, projectIdentifier: true)
            || !IsOptionalIdentifier(query.VoiceAlias, 64, projectIdentifier: false))
        {
            throw new ArgumentException("Usage filters are invalid.");
        }
    }

    private static bool IsOptionalIdentifier(
        string? value,
        int maximumLength,
        bool projectIdentifier) =>
        value is null
        || value.Length is >= 1
            && value.Length <= maximumLength
            && string.Equals(value, value.Trim(), StringComparison.Ordinal)
            && value.All(character =>
                character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-'
                || projectIdentifier && character is '_' or '.' or ':');
}

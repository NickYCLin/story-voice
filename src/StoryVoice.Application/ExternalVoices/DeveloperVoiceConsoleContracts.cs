namespace StoryVoice.Application.ExternalVoices;

public static class DeveloperVoiceProjectStatuses
{
    public const string NotYetEffective = "not-yet-effective";
    public const string Active = "active";
    public const string ExpiringSoon = "expiring-soon";
    public const string Expired = "expired";
}

public static class DeveloperVoiceGrantStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}

public sealed record DeveloperVoiceGrantSummary(
    string VoiceAlias,
    string Status,
    DateTimeOffset? RevokedAtUtc);

public sealed record DeveloperVoiceProjectSummary(
    string KeyId,
    string DisplayName,
    string ProjectId,
    string AccessTier,
    string TokenPrefix,
    string ConsumerFamilyId,
    string TerritoryCountryCode,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Status,
    IReadOnlyList<DeveloperVoiceGrantSummary> Voices);

public sealed record DeveloperVoiceConsoleOverview(
    bool ServiceEnabled,
    int RequestsPerMinute,
    int MaximumTextCharacters,
    int MaximumTextUtf8Bytes,
    IReadOnlyList<DeveloperVoiceProjectSummary> Projects);

public interface IDeveloperVoiceConsoleService
{
    Task<DeveloperVoiceConsoleOverview> GetOverviewAsync(CancellationToken cancellationToken);
}

public static class DeveloperVoiceCredentialStatuses
{
    public const string NotYetEffective = "not-yet-effective";
    public const string Active = "active";
    public const string RevocationScheduled = "revocation-scheduled";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

public sealed record DeveloperVoiceCredentialSummary(
    Guid? Id,
    string KeyId,
    string Name,
    string ProjectId,
    string AccessTier,
    string TokenPrefix,
    bool Managed,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string Status);

public sealed record DeveloperVoiceCredentialList(
    IReadOnlyList<DeveloperVoiceCredentialSummary> Credentials);

public sealed record CreateDeveloperVoiceCredentialRequest(
    string ProjectId,
    string Name);

public sealed record RotateDeveloperVoiceCredentialRequest(
    int OverlapMinutes);

public sealed record IssuedDeveloperVoiceCredential(
    DeveloperVoiceCredentialSummary Credential,
    string AccessToken,
    string Notice);

public sealed record DeveloperVoiceCredentialAuditSummary(
    Guid Id,
    string CredentialKeyId,
    string Action,
    DateTimeOffset OccurredAtUtc,
    string? RelatedCredentialKeyId);

public interface IDeveloperVoiceCredentialService
{
    Task<DeveloperVoiceCredentialList> ListAsync(CancellationToken cancellationToken);

    Task<IssuedDeveloperVoiceCredential?> CreateAsync(
        CreateDeveloperVoiceCredentialRequest request,
        CancellationToken cancellationToken);

    Task<IssuedDeveloperVoiceCredential?> RotateAsync(
        Guid credentialId,
        RotateDeveloperVoiceCredentialRequest request,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(Guid credentialId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeveloperVoiceCredentialAuditSummary>> ListAuditAsync(
        CancellationToken cancellationToken);
}

public static class ExternalVoiceUsageOutcomes
{
    public const string Succeeded = "succeeded";
    public const string RequestCancelled = "request_cancelled";
}

public sealed record ExternalVoiceUsageWrite(
    Guid OwnerId,
    string ConsumerKeyId,
    string CredentialKeyId,
    string ProjectId,
    string AccessTier,
    string RequestId,
    string? VoiceAlias,
    DateTimeOffset OccurredAtUtc,
    int StatusCode,
    string Outcome,
    int DurationMilliseconds,
    int? TextCharacters,
    long ResponseBytes,
    long AudioDurationMilliseconds);

public interface IExternalVoiceUsageRecorder
{
    Task RecordAsync(ExternalVoiceUsageWrite usage, CancellationToken cancellationToken);
}

public sealed record DeveloperVoiceUsageQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? ProjectId,
    string? VoiceAlias,
    int ActivityLimit);

public sealed record DeveloperVoiceUsageSummary(
    int TotalRequests,
    int SuccessfulRequests,
    double SuccessRatePercent,
    int RateLimitedRequests,
    double AverageLatencyMilliseconds,
    long OutputBytes,
    long OutputDurationMilliseconds);

public sealed record DeveloperVoiceUsageActivity(
    string RequestId,
    string ProjectId,
    string? VoiceAlias,
    DateTimeOffset OccurredAtUtc,
    int StatusCode,
    string Outcome,
    int DurationMilliseconds,
    int? TextCharacters,
    long ResponseBytes,
    long AudioDurationMilliseconds);

public sealed record DeveloperVoiceUsageReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DeveloperVoiceUsageSummary Summary,
    IReadOnlyList<DeveloperVoiceUsageActivity> Activities);

public interface IDeveloperVoiceUsageService
{
    Task<DeveloperVoiceUsageReport> GetUsageAsync(
        DeveloperVoiceUsageQuery query,
        CancellationToken cancellationToken);
}

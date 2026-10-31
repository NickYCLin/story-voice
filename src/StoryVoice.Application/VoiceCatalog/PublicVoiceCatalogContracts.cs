namespace StoryVoice.Application.VoiceCatalog;

public static class PublicVoiceCatalogStatus
{
    public const string Available = "available";
}

public static class PublicVoiceCatalogCtaKinds
{
    public const string ViewPlans = "view-plans";
}

public sealed record PublicVoiceCatalogCard(
    string Alias,
    string DisplayName,
    string Subtitle,
    string Disclosure,
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> UseCases,
    string SampleUrl,
    bool CanPreview,
    string CtaKind,
    bool SubscriptionAvailable,
    string Status);

public sealed record PublicVoiceDemo(
    byte[] Content,
    string ContentType);

public sealed record PublicVoiceCatalogDetail(
    PublicVoiceCatalogCard Voice,
    PublicVoiceLicenseSummary License);

public sealed record PublicVoiceLicenseSummary(
    bool CommercialUseAllowed,
    bool PublicDistributionAllowed,
    bool CrossProjectApiAllowed,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string TerritoryMode,
    IReadOnlyList<string> TerritoryCountryCodes);

public interface IPublicVoiceCatalogService
{
    Task<IReadOnlyList<PublicVoiceCatalogCard>> GetVoicesAsync(
        CancellationToken cancellationToken);

    Task<PublicVoiceCatalogDetail?> GetVoiceAsync(
        string alias,
        CancellationToken cancellationToken);

    Task<PublicVoiceDemo?> GetDemoAsync(
        string alias,
        CancellationToken cancellationToken);
}

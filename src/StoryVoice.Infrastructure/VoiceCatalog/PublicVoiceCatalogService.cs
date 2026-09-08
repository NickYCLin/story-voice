using System.Security;
using System.Text;
using Microsoft.Extensions.Options;
using StoryVoice.Application.VoiceCatalog;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Infrastructure.VoiceCatalog;

internal sealed class PublicVoiceCatalogService(
    IOptions<VoiceCatalogOptions> options,
    IOptions<LocalClonePreviewOptions> localCloneOptions,
    TimeProvider timeProvider) : IPublicVoiceCatalogService
{
    private const string WaveContentType = "audio/wav";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly VoiceCatalogOptions _options = options.Value;

    public async Task<IReadOnlyList<PublicVoiceCatalogCard>> GetVoicesAsync(
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _options.Entries is null || _options.Entries.Count == 0)
        {
            return [];
        }

        var physicalRoot = TryResolveRoot();
        if (physicalRoot is null)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        var cards = new List<PublicVoiceCatalogCard>();
        foreach (var (alias, entry) in _options.Entries.OrderBy(candidate => candidate.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null)
            {
                continue;
            }

            var validated = await ValidateEntryAsync(
                    physicalRoot,
                    alias,
                    entry,
                    now,
                    includeDemoContent: false,
                    cancellationToken);
            if (validated is null)
            {
                continue;
            }

            cards.Add(CreateCard(alias, validated.Authorization));
        }

        return cards;
    }

    public async Task<PublicVoiceCatalogDetail?> GetVoiceAsync(
        string alias,
        CancellationToken cancellationToken)
    {
        var validated = await GetEntryAsync(alias, includeDemoContent: false, cancellationToken);
        if (validated is null)
        {
            return null;
        }

        var authorization = validated.Authorization;
        return new PublicVoiceCatalogDetail(
            CreateCard(alias, authorization),
            new PublicVoiceLicenseSummary(
                CommercialUseAllowed: true,
                PublicDistributionAllowed: true,
                CrossProjectApiAllowed: true,
                authorization.EffectiveAtUtc,
                authorization.ExpiresAtUtc,
                authorization.TerritoryMode,
                authorization.TerritoryCountryCodes.ToArray()));
    }

    public async Task<PublicVoiceDemo?> GetDemoAsync(
        string alias,
        CancellationToken cancellationToken)
    {
        var validated = await GetEntryAsync(alias, includeDemoContent: true, cancellationToken);
        return validated?.Demo is null
            ? null
            : new PublicVoiceDemo(validated.Demo, WaveContentType);
    }

    private async Task<ValidatedCatalogEntry?> GetEntryAsync(
        string alias,
        bool includeDemoContent,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !VoiceCatalogOptionsValidator.IsCanonicalAlias(alias))
        {
            return null;
        }

        var configuredEntry = _options.Entries?
            .Where(candidate => string.Equals(candidate.Key, alias, StringComparison.Ordinal))
            .Select(candidate => candidate.Value)
            .SingleOrDefault();
        if (configuredEntry is null)
        {
            return null;
        }

        var physicalRoot = TryResolveRoot();
        if (physicalRoot is null)
        {
            return null;
        }

        return await ValidateEntryAsync(
            physicalRoot,
            alias,
            configuredEntry,
            timeProvider.GetUtcNow(),
            includeDemoContent,
            cancellationToken);
    }

    private static PublicVoiceCatalogCard CreateCard(
        string alias,
        SyntheticVoiceAuthorizationEvidence authorization) => new(
            alias,
            authorization.DisplayName,
            authorization.AttributionText ?? string.Empty,
            "AI 合成語音",
            authorization.Styles.ToArray(),
            authorization.UseCases.ToArray(),
            $"/api/public/v1/voices/{alias}/demo",
            CanPreview: true,
            PublicVoiceCatalogCtaKinds.ViewPlans,
            SubscriptionAvailable: true,
            PublicVoiceCatalogStatus.Available);

    private string? TryResolveRoot()
    {
        try
        {
            return PrivateAssetFileValidator.ResolveExistingPhysicalPath(
                Path.GetFullPath(_options.AssetRootPath),
                requireDirectory: true);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return null;
        }
    }

    private async Task<ValidatedCatalogEntry?> ValidateEntryAsync(
        string physicalRoot,
        string alias,
        VoiceCatalogEntryOptions entry,
        DateTimeOffset now,
        bool includeDemoContent,
        CancellationToken cancellationToken)
    {
        try
        {
            var originPath = PrivateAssetFileValidator.ResolvePrivateFile(
                physicalRoot,
                entry.SyntheticVoiceAuthorizationRelativePath);
            var originContent = await PrivateAssetFileValidator.ReadBoundedAsync(
                originPath,
                SyntheticVoiceAuthorizationValidator.MaximumBytes,
                cancellationToken);
            PrivateAssetFileValidator.VerifySha256(
                originContent,
                entry.SyntheticVoiceAuthorizationSha256);
            var syntheticAuthorization = SyntheticVoiceAuthorizationValidator.Validate(
                originContent,
                alias,
                now);
            await ValidateSyntheticSupportingEvidenceAsync(
                physicalRoot,
                entry,
                syntheticAuthorization,
                cancellationToken);
            await ValidateSyntheticProfileAssetsAsync(
                syntheticAuthorization,
                cancellationToken);

            var demoPath = PrivateAssetFileValidator.ResolvePrivateFile(
                physicalRoot,
                entry.DemoAudioRelativePath);
            var demo = await PrivateAssetFileValidator.ReadBoundedAsync(
                demoPath,
                _options.MaximumDemoBytes,
                cancellationToken);
            PrivateAssetFileValidator.VerifySha256(
                demo,
                syntheticAuthorization.FixedDemoSha256);
            LocalClonePcmWaveValidator.ValidatePublicDemo(demo, _options.MaximumDemoBytes);
            return new ValidatedCatalogEntry(
                syntheticAuthorization,
                includeDemoContent ? demo : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return null;
        }
    }

    internal static async Task ValidateSyntheticSupportingEvidenceAsync(
        string physicalRoot,
        VoiceCatalogEntryOptions entry,
        SyntheticVoiceAuthorizationEvidence origin,
        CancellationToken cancellationToken)
    {
        var manifestPath = PrivateAssetFileValidator.ResolvePrivateFile(
            physicalRoot,
            entry.GenerationManifestRelativePath);
        var manifest = await PrivateAssetFileValidator.ReadBoundedAsync(
            manifestPath,
            SyntheticVoiceAuthorizationValidator.MaximumGenerationManifestBytes,
            cancellationToken);
        if (manifest.Length == 0)
        {
            throw new InvalidDataException("Synthetic voice generation manifest is empty.");
        }

        PrivateAssetFileValidator.VerifySha256(manifest, origin.GenerationManifestSha256);

        var termsPath = PrivateAssetFileValidator.ResolvePrivateFile(
            physicalRoot,
            entry.TermsSnapshotRelativePath);
        var terms = await PrivateAssetFileValidator.ReadBoundedAsync(
            termsPath,
            SyntheticVoiceAuthorizationValidator.MaximumTermsSnapshotBytes,
            cancellationToken);
        if (terms.Length == 0)
        {
            throw new InvalidDataException("Synthetic voice terms snapshot is empty.");
        }

        PrivateAssetFileValidator.VerifySha256(terms, origin.TermsSnapshotSha256);
    }

    private async Task ValidateSyntheticProfileAssetsAsync(
        SyntheticVoiceAuthorizationEvidence origin,
        CancellationToken cancellationToken)
    {
        var local = localCloneOptions.Value;
        if (local.AllowedProfiles is null
            || !local.AllowedProfiles.TryGetValue(
                origin.CharacterProfileId.ToString("D"),
                out var profile)
            || profile is null
            || !string.Equals(
                profile.ExpectedReferenceAudioSha256,
                origin.ReferenceAudioSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                profile.ExpectedTranscriptSha256,
                origin.TranscriptCanonicalSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Synthetic voice profile binding is unavailable.");
        }

        var profileRoot = PrivateAssetFileValidator.ResolveExistingPhysicalPath(
            Path.GetFullPath(local.AssetRootPath),
            requireDirectory: true);
        var referencePath = PrivateAssetFileValidator.ResolvePrivateFile(
            profileRoot,
            profile.ReferenceAudioRelativePath);
        var reference = await PrivateAssetFileValidator.ReadBoundedAsync(
            referencePath,
            LocalClonePreviewOptions.MaximumReferenceAudioBytes,
            cancellationToken);
        PrivateAssetFileValidator.VerifySha256(reference, origin.ReferenceAudioSha256);
        LocalClonePcmWaveValidator.ValidateReference(reference);

        var transcriptPath = PrivateAssetFileValidator.ResolvePrivateFile(
            profileRoot,
            profile.TranscriptRelativePath);
        var transcript = await PrivateAssetFileValidator.ReadBoundedAsync(
            transcriptPath,
            LocalClonePreviewOptions.MaximumTranscriptBytes,
            cancellationToken);
        var canonicalTranscript = CharacterVoiceTranscriptCanonicalizer.Normalize(
            StrictUtf8.GetString(transcript));
        PrivateAssetFileValidator.VerifySha256(
            Encoding.UTF8.GetBytes(canonicalTranscript),
            origin.TranscriptCanonicalSha256);
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException;

    private sealed record ValidatedCatalogEntry(
        SyntheticVoiceAuthorizationEvidence Authorization,
        byte[]? Demo);
}

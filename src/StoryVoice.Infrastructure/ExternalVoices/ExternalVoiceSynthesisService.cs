using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.VoiceCatalog;

namespace StoryVoice.Infrastructure.ExternalVoices;

internal sealed class ExternalVoiceSynthesisService(
    IOptions<ExternalVoiceApiOptions> options,
    IOptions<LocalClonePreviewOptions> localCloneOptions,
    IOptions<VoiceCatalogOptions> voiceCatalogOptions,
    LocalCloneProfileSynthesizer profileSynthesizer,
    ExternalVoiceIdempotencyCoordinator idempotencyCoordinator,
    ExternalVoiceConcurrencyGate concurrencyGate,
    TimeProvider timeProvider) : IExternalVoiceSynthesisService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ExternalVoiceAudio> SynthesizeAsync(
        string consumerKeyId,
        ExternalVoiceSynthesisRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw InvalidRequest();
        }
        var currentOptions = options.Value;
        if (!currentOptions.Enabled)
        {
            throw new ExternalVoiceSynthesisException(
                ExternalVoiceSynthesisFailureKind.Disabled);
        }

        if (!ExternalVoiceApiOptionsValidator.IsCanonicalConsumerKeyId(consumerKeyId)
            || !currentOptions.Consumers.TryGetValue(consumerKeyId, out var consumer)
            || consumer is null)
        {
            throw VoiceNotAvailable();
        }

        var now = timeProvider.GetUtcNow();
        if (consumer.OwnerId == Guid.Empty
            || !ExternalVoiceAccessTiers.IsSupported(consumer.AccessTier)
            || consumer.EffectiveAtUtc > now
            || consumer.ExpiresAtUtc <= now)
        {
            throw VoiceNotAvailable();
        }

        var normalized = NormalizeRequest(request, currentOptions);
        ValidateIdempotencyKey(idempotencyKey);
        if (!consumer.AllowedVoices.TryGetValue(normalized.Voice, out var grant)
            || grant is null
            || !IsGrantConfigured(grant))
        {
            throw VoiceNotAvailable();
        }

        var characterProfileId = await ValidateAuthorizationChainAsync(
            consumerKeyId,
            consumer,
            normalized.Voice,
            grant,
            now,
            cancellationToken);
        var availability = await profileSynthesizer.GetAvailabilityAsync(
            consumer.OwnerId,
            characterProfileId,
            cancellationToken);
        if (!availability.Available)
        {
            throw VoiceNotAvailable();
        }

        var requestHash = ComputeRequestHash(normalized);
        return await idempotencyCoordinator.ExecuteAsync(
            consumerKeyId,
            idempotencyKey,
            requestHash,
            () => SynthesizeCoreAsync(
                consumer.OwnerId,
                characterProfileId,
                normalized.Text,
                currentOptions.MaximumResponseBytes,
                cancellationToken));
    }

    private async Task<ExternalVoiceAudio> SynthesizeCoreAsync(
        Guid ownerId,
        Guid characterProfileId,
        string text,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        using var lease = concurrencyGate.TryEnter();
        if (lease is null)
        {
            throw new ExternalVoiceSynthesisException(
                ExternalVoiceSynthesisFailureKind.RateLimited,
                retryAfterSeconds: 1);
        }

        try
        {
            var audio = await profileSynthesizer.SynthesizeAsync(
                ownerId,
                characterProfileId,
                text,
                cancellationToken);
            if (!string.Equals(audio.ContentType, "audio/wav", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unexpected local clone content type.");
            }

            var durationMilliseconds = LocalClonePcmWaveValidator.ValidateExternalOutput(
                audio.Content,
                maximumResponseBytes);
            return new ExternalVoiceAudio(
                audio.Content.ToArray(),
                "audio/wav",
                durationMilliseconds);
        }
        catch (ExternalVoiceSynthesisException)
        {
            throw;
        }
        catch (LocalClonePreviewUnavailableException exception)
        {
            throw exception.FailureKind == LocalClonePreviewFailureKind.NotConfigured
                ? VoiceNotAvailable()
                : SynthesisUnavailable(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is only observable before the local synthesizer's GPU commit
            // boundary. After submission it deliberately waits with CancellationToken.None.
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException)
        {
            throw SynthesisUnavailable(exception);
        }
        catch (Exception exception)
        {
            throw SynthesisUnavailable(exception);
        }
    }

    private async Task<Guid> ValidateAuthorizationChainAsync(
        string consumerKeyId,
        ExternalVoiceConsumerOptions consumer,
        string voiceAlias,
        ExternalVoiceGrantOptions grant,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var physicalRoot = PrivateAssetFileValidator.ResolveExistingPhysicalPath(
                Path.GetFullPath(localCloneOptions.Value.AssetRootPath),
                requireDirectory: true);

            var evidencePath = PrivateAssetFileValidator.ResolvePrivateFile(
                physicalRoot,
                grant.AuthorizationEvidenceRelativePath);
            var evidence = await PrivateAssetFileValidator.ReadBoundedAsync(
                evidencePath,
                Math.Max(
                    ExternalVoiceAuthorizationEvidenceValidator.MaximumBytes,
                    ExternalVoiceDevelopmentGrantValidator.MaximumBytes),
                cancellationToken);
            PrivateAssetFileValidator.VerifySha256(
                evidence,
                grant.AuthorizationEvidenceSha256);

            ExternalVoiceUsageAuthorizationEvidence? usageAuthorization = null;
            ExternalVoiceDevelopmentGrantEvidence? developmentGrant = null;
            Guid characterProfileId;
            if (string.Equals(
                    consumer.AccessTier,
                    ExternalVoiceAccessTiers.PrivateDevelopment,
                    StringComparison.Ordinal))
            {
                developmentGrant = ExternalVoiceDevelopmentGrantValidator.Validate(
                    evidence,
                    consumerKeyId,
                    consumer,
                    voiceAlias,
                    grant,
                    now);
                characterProfileId = developmentGrant.CharacterProfileId;
            }
            else if (string.Equals(
                         consumer.AccessTier,
                         ExternalVoiceAccessTiers.SubscriptionCommercial,
                         StringComparison.Ordinal))
            {
                usageAuthorization = ExternalVoiceAuthorizationEvidenceValidator.Validate(
                    evidence,
                    consumerKeyId,
                    consumer,
                    voiceAlias,
                    grant,
                    now);
                characterProfileId = usageAuthorization.CharacterProfileId;
            }
            else
            {
                throw new InvalidDataException("External voice access tier is unavailable.");
            }

            if (!localCloneOptions.Value.AllowedProfiles.TryGetValue(
                    characterProfileId.ToString("D"),
                    out var profileAsset)
                || profileAsset is null)
            {
                throw new InvalidDataException("External voice profile assets are unavailable.");
            }

            var referencePath = PrivateAssetFileValidator.ResolvePrivateFile(
                physicalRoot,
                profileAsset.ReferenceAudioRelativePath);
            var reference = await PrivateAssetFileValidator.ReadBoundedAsync(
                referencePath,
                LocalClonePreviewOptions.MaximumReferenceAudioBytes,
                cancellationToken);
            PrivateAssetFileValidator.VerifySha256(
                reference,
                profileAsset.ExpectedReferenceAudioSha256);
            if (developmentGrant is not null
                && !string.Equals(
                    developmentGrant.ReferenceAudioSha256,
                    profileAsset.ExpectedReferenceAudioSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "External voice development grant does not match the reference audio.");
            }
            LocalClonePcmWaveValidator.ValidateReference(reference);

            var transcriptPath = PrivateAssetFileValidator.ResolvePrivateFile(
                physicalRoot,
                profileAsset.TranscriptRelativePath);
            var transcriptBytes = await PrivateAssetFileValidator.ReadBoundedAsync(
                transcriptPath,
                LocalClonePreviewOptions.MaximumTranscriptBytes,
                cancellationToken);
            var canonicalTranscript = CharacterVoiceTranscriptCanonicalizer.Normalize(
                StrictUtf8.GetString(transcriptBytes));
            PrivateAssetFileValidator.VerifySha256(
                Encoding.UTF8.GetBytes(canonicalTranscript),
                profileAsset.ExpectedTranscriptSha256);
            if (developmentGrant is not null
                && !string.Equals(
                    developmentGrant.TranscriptCanonicalSha256,
                    profileAsset.ExpectedTranscriptSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "External voice development grant does not match the transcript.");
            }

            if (usageAuthorization is not null)
            {
                await ValidateCommercialVoiceChainAsync(
                    consumer,
                    voiceAlias,
                    usageAuthorization,
                    profileAsset,
                    now,
                    cancellationToken);
            }

            return characterProfileId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException)
        {
            throw new ExternalVoiceSynthesisException(
                ExternalVoiceSynthesisFailureKind.VoiceNotAvailable,
                retryAfterSeconds: null,
                exception);
        }
    }

    private async Task ValidateCommercialVoiceChainAsync(
        ExternalVoiceConsumerOptions consumer,
        string voiceAlias,
        ExternalVoiceUsageAuthorizationEvidence usageAuthorization,
        LocalClonePreviewAssetOptions profileAsset,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var catalog = voiceCatalogOptions.Value;
        if (catalog.Entries is null)
        {
            throw new InvalidDataException(
                "Commercial external voice grant does not match the configured voice entries.");
        }

        var matches = catalog.Entries
            .Where(candidate => string.Equals(candidate.Key, voiceAlias, StringComparison.Ordinal))
            .Select(candidate => candidate.Value)
            .ToArray();
        if (matches is not [{ } entry])
        {
            throw new InvalidDataException("Commercial synthetic voice entry is unavailable.");
        }

        if (!string.Equals(
                entry.SyntheticVoiceAuthorizationSha256,
                usageAuthorization.SyntheticVoiceAuthorizationSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "External voice usage does not reference the configured voice authorization.");
        }

        var physicalRoot = PrivateAssetFileValidator.ResolveExistingPhysicalPath(
            Path.GetFullPath(catalog.AssetRootPath),
            requireDirectory: true);

        var authorizationPath = PrivateAssetFileValidator.ResolvePrivateFile(
            physicalRoot,
            entry.SyntheticVoiceAuthorizationRelativePath);
        var authorizationContent = await PrivateAssetFileValidator.ReadBoundedAsync(
            authorizationPath,
            SyntheticVoiceAuthorizationValidator.MaximumBytes,
            cancellationToken);
        PrivateAssetFileValidator.VerifySha256(
            authorizationContent,
            entry.SyntheticVoiceAuthorizationSha256);
        var authorization = SyntheticVoiceAuthorizationValidator.Validate(
            authorizationContent,
            voiceAlias,
            now);
        if (authorization.OwnerId != consumer.OwnerId
            || authorization.CharacterProfileId != usageAuthorization.CharacterProfileId
            || !authorization.AllowedConsumerFamilies.Contains(
                consumer.ConsumerFamilyId,
                StringComparer.Ordinal)
            || !TerritoryIncludes(authorization, consumer.TerritoryCountryCode)
            || !string.Equals(
                authorization.ReferenceAudioSha256,
                profileAsset.ExpectedReferenceAudioSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                authorization.TranscriptCanonicalSha256,
                profileAsset.ExpectedTranscriptSha256,
                StringComparison.Ordinal)
            || usageAuthorization.EffectiveAtUtc < authorization.EffectiveAtUtc
            || usageAuthorization.ExpiresAtUtc > authorization.ExpiresAtUtc)
        {
            throw new InvalidDataException(
                "Synthetic voice authorization does not match the external voice grant.");
        }

        await PublicVoiceCatalogService.ValidateSyntheticSupportingEvidenceAsync(
            physicalRoot,
            entry,
            authorization,
            cancellationToken);

        var demoPath = PrivateAssetFileValidator.ResolvePrivateFile(
            physicalRoot,
            entry.DemoAudioRelativePath);
        var demo = await PrivateAssetFileValidator.ReadBoundedAsync(
            demoPath,
            catalog.MaximumDemoBytes,
            cancellationToken);
        PrivateAssetFileValidator.VerifySha256(demo, authorization.FixedDemoSha256);
        LocalClonePcmWaveValidator.ValidatePublicDemo(demo, catalog.MaximumDemoBytes);
    }

    private static bool TerritoryIncludes(
        SyntheticVoiceAuthorizationEvidence authorization,
        string countryCode) =>
        string.Equals(authorization.TerritoryMode, "worldwide", StringComparison.Ordinal)
            ? authorization.TerritoryCountryCodes.Count == 0
            : string.Equals(
                    authorization.TerritoryMode,
                    "country-list",
                    StringComparison.Ordinal)
                && authorization.TerritoryCountryCodes.Contains(
                    countryCode,
                    StringComparer.Ordinal);

    private static NormalizedExternalVoiceRequest NormalizeRequest(
        ExternalVoiceSynthesisRequest request,
        ExternalVoiceApiOptions options)
    {
        var voice = NormalizePrintable(request.Voice);
        var text = NormalizePrintable(request.Text);
        int utf8Bytes;
        try
        {
            utf8Bytes = StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw InvalidRequest(exception);
        }

        if (!ExternalVoiceApiOptionsValidator.IsCanonicalVoiceAlias(voice)
            || text.EnumerateRunes().Count() > options.MaximumTextCharacters
            || utf8Bytes > options.MaximumTextUtf8Bytes)
        {
            throw InvalidRequest();
        }

        return new NormalizedExternalVoiceRequest(voice, text);
    }

    private static string NormalizePrintable(string? value)
    {
        if (value is null)
        {
            throw InvalidRequest();
        }

        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        }
        catch (ArgumentException exception)
        {
            throw InvalidRequest(exception);
        }

        if (normalized.Length == 0
            || normalized.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format))
        {
            throw InvalidRequest();
        }

        return normalized;
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (idempotencyKey is not { Length: >= 16 and <= 64 }
            || idempotencyKey.Any(character => character is not (
                >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-')))
        {
            throw InvalidRequest();
        }
    }

    private static bool IsGrantConfigured(ExternalVoiceGrantOptions grant) =>
        ExternalVoiceApiOptionsValidator.IsCanonicalSha256(
            grant.AuthorizationEvidenceSha256)
        && ExternalVoiceApiOptionsValidator.IsValidRelativePath(
            grant.AuthorizationEvidenceRelativePath)
        && grant.RevokedAtUtc is null;

    private static byte[] ComputeRequestHash(NormalizedExternalVoiceRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashField(hash, request.Voice);
        AppendHashField(hash, request.Text);
        return hash.GetHashAndReset();
    }

    private static void AppendHashField(IncrementalHash hash, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static ExternalVoiceSynthesisException InvalidRequest(Exception? inner = null) =>
        new(ExternalVoiceSynthesisFailureKind.InvalidRequest, null, inner);

    private static ExternalVoiceSynthesisException VoiceNotAvailable() =>
        new(ExternalVoiceSynthesisFailureKind.VoiceNotAvailable);

    private static ExternalVoiceSynthesisException SynthesisUnavailable(Exception inner) =>
        new(ExternalVoiceSynthesisFailureKind.SynthesisUnavailable, null, inner);

    private sealed record NormalizedExternalVoiceRequest(
        string Voice,
        string Text);
}

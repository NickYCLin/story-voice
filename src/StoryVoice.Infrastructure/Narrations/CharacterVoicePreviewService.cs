using System.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Narrations;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Infrastructure.Narrations;

internal sealed class CharacterVoicePreviewService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser,
    ILocalCloneGatewayClient gatewayClient,
    IOptions<LocalClonePreviewOptions> options,
    IOptions<CharacterVoiceStorageOptions> storageOptions) : ICharacterVoicePreviewService
{
    private const int MaximumPreviewTextLength = 200;

    public async Task<VoiceProfilePreviewAudio?> PreviewAsync(
        Guid characterProfileId,
        Guid profileId,
        PreviewVoiceProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = request.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("試講文字不可為空白。", nameof(request));
        }

        if (text.Length > MaximumPreviewTextLength)
        {
            throw new ArgumentException($"試講文字不可超過 {MaximumPreviewTextLength} 個字元。", nameof(request));
        }

        var ownerId = EnsureCurrentOwnerId();
        var profile = await dbContext.CharacterVoiceProfiles.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.OwnerId == ownerId
                && candidate.CharacterProfileId == characterProfileId
                && candidate.Id == profileId,
            cancellationToken);
        if (profile is null)
        {
            return null;
        }

        if (profile.Mode == CharacterVoiceProfileMode.Design
            && !ThreeWaSynthesisCapabilities.SupportsVoiceDesign)
        {
            throw new InvalidOperationException(
                ThreeWaSynthesisCapabilities.DesignVoiceUnavailableMessage);
        }

        if (profile.Status != CharacterVoiceProfileStatus.Ready)
        {
            throw new InvalidOperationException("這組聲線還沒就緒，無法試講。");
        }

        if (profile.Mode == CharacterVoiceProfileMode.Clone)
        {
            var privateEvaluationAuthorized = await dbContext.CharacterVoiceProfileOperations
                .AsNoTracking()
                .AnyAsync(
                    operation => operation.OwnerId == ownerId
                        && operation.CharacterProfileId == characterProfileId
                        && operation.NewProfileId == profile.Id
                        && operation.State == CharacterVoiceProfileOperationState.Activated
                        && operation.EvidenceVersion == CharacterVoiceConsentEvidence.CurrentEvidenceVersion
                        && operation.AttestationVersion == CharacterVoiceConsentEvidence.CurrentAttestationVersion
                        && operation.PrivateEvaluationAllowed,
                    cancellationToken);
            if (!privateEvaluationAuthorized)
            {
                throw new InvalidOperationException(
                    "這組克隆聲線缺少可驗證的私人試音授權，無法試講。");
            }
        }

        if (!options.Value.Enabled)
        {
            throw new LocalClonePreviewUnavailableException(LocalClonePreviewFailureKind.Disabled);
        }

        if (!await dbContext.CharacterProfiles.AsNoTracking().AnyAsync(
                character => character.Id == characterProfileId && character.OwnerId == ownerId
                    && character.IsActive, cancellationToken))
        {
            throw new LocalClonePreviewUnavailableException(LocalClonePreviewFailureKind.NotConfigured);
        }

        byte[] reference;
        string transcript;
        try
        {
            if (string.IsNullOrWhiteSpace(profile.ReferenceAudioRelativePath)
                || string.IsNullOrWhiteSpace(profile.ReferenceAudioSha256)
                || profile.TranscriptConfirmedAt is null
                || string.IsNullOrWhiteSpace(profile.Transcript))
            {
                throw new InvalidDataException("The managed reference is incomplete.");
            }

            var path = CharacterVoiceReferenceFiles.Resolve(
                storageOptions.Value.RootPath, profile.ReferenceAudioRelativePath);
            reference = await PrivateAssetFileValidator.ReadBoundedAsync(
                path, LocalClonePreviewOptions.MaximumReferenceAudioBytes, cancellationToken);
            PrivateAssetFileValidator.VerifySha256(reference, profile.ReferenceAudioSha256);
            LocalClonePcmWaveValidator.ValidateReference(reference);
            transcript = CharacterVoiceTranscriptCanonicalizer.Normalize(profile.Transcript);
            if (System.Text.Encoding.UTF8.GetByteCount(transcript) > LocalClonePreviewOptions.MaximumTranscriptBytes)
            {
                throw new InvalidDataException("The managed transcript is too large.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException
            or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            throw new LocalClonePreviewUnavailableException(LocalClonePreviewFailureKind.AssetInvalid, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        // One submission to the pinned private gateway; never retry or fall back to
        // an external provider. A disconnected browser must not cancel in-flight GPU work.
        var audio = await gatewayClient.SynthesizeAsync(
            new LocalCloneGatewayRequest(text, transcript, reference), CancellationToken.None);
        return new VoiceProfilePreviewAudio(audio.Content, audio.ContentType);
    }

    private Guid EnsureCurrentOwnerId()
    {
        if (currentUser.UserId == Guid.Empty)
        {
            throw new InvalidOperationException("目前使用者識別碼無效。");
        }

        return currentUser.UserId;
    }

}

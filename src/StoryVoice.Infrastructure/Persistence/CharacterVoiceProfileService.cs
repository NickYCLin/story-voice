using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Characters;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

/// <summary>
/// Owner-scoped lifecycle service for <see cref="CharacterVoiceProfile"/>, hanging off a
/// <see cref="CharacterProfile"/> library entry rather than any one series — the same character's
/// voices are reused everywhere that character is cast. Design-mode creation is fail-closed while
/// 3wa does not forward voice_prompt to VoxCPM2; Clone-mode profiles proxy the 3wa Cluster API's
/// profile_prepare/status/confirm sequence, keeping the locally stored WAV + confirmed transcript
/// as the canonical, rebuildable asset behind whatever opaque task id the pinned station currently
/// has.
/// </summary>
internal sealed class CharacterVoiceProfileService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser,
    LocalCharacterVoiceAudioStorage audioStorage,
    IThreeWaVoiceProfileClient threeWaClient,
    IOptions<ThreeWaAiHubOptions> threeWaOptions) : ICharacterVoiceProfileService
{
    // Durable path 的遠端呼叫上限：取代 CancellationToken.None，
    // 涵蓋 10 MiB WAV 上傳仍綽綽有餘，但擋住無限期的 body 停滯。
    private static readonly TimeSpan DurableRemoteCallTimeout = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<CharacterVoiceProfileResponse>?> ListAsync(
        Guid characterProfileId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var character = await LoadCharacterProfileAsync(ownerId, characterProfileId, cancellationToken);
        if (character is null)
        {
            return null;
        }

        var profiles = await dbContext.CharacterVoiceProfiles
            .Where(profile => profile.OwnerId == ownerId && profile.CharacterProfileId == characterProfileId)
            .OrderBy(profile => profile.Kind)
            .ThenBy(profile => profile.SceneCode)
            .ToListAsync(cancellationToken);
        return profiles.Select(ToResponse).ToArray();
    }

    public async Task<IReadOnlyList<CharacterVoiceProfileOperationResponse>?> ListCloneOperationsAsync(
        Guid characterProfileId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        if (await LoadCharacterProfileAsync(ownerId, characterProfileId, cancellationToken) is null)
        {
            return null;
        }

        var operations = await dbContext.CharacterVoiceProfileOperations
            .AsNoTracking()
            .Where(operation => operation.OwnerId == ownerId
                && operation.CharacterProfileId == characterProfileId)
            .OrderByDescending(operation => operation.CreatedAt)
            .ToListAsync(cancellationToken);
        return operations.Select(ToOperationResponse).ToArray();
    }

    public async Task<CharacterVoiceProfileResponse?> ResumeCloneActivationAsync(
        Guid characterProfileId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        if (await LoadCharacterProfileAsync(ownerId, characterProfileId, cancellationToken) is null)
        {
            return null;
        }

        var operation = await dbContext.CharacterVoiceProfileOperations.SingleOrDefaultAsync(
            candidate => candidate.OwnerId == ownerId
                && candidate.CharacterProfileId == characterProfileId
                && candidate.Id == operationId,
            cancellationToken);
        if (operation is null)
        {
            return null;
        }

        if (operation.State == CharacterVoiceProfileOperationState.Activated)
        {
            var activatedProfile = await LoadOperationProfileAsync(operation, cancellationToken);
            return activatedProfile is null
                ? throw new InvalidOperationException("activated_profile_missing")
                : ToResponse(activatedProfile);
        }

        if (operation.State == CharacterVoiceProfileOperationState.NeedsAttention
            && operation.SafeErrorCode == "local_activation_uncertain")
        {
            var resolvedProfile = await LoadOperationProfileAsync(operation, cancellationToken);
            if (resolvedProfile is not null)
            {
                operation.MarkActivationResolved(DateTimeOffset.UtcNow);
                await dbContext.SaveChangesAsync(CancellationToken.None);
                return ToResponse(resolvedProfile);
            }

            operation.ResumeActivation(DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        else if (operation.State != CharacterVoiceProfileOperationState.RemotePrepared)
        {
            throw new InvalidOperationException(GetOperationAttentionCode(operation));
        }

        if (operation.RemoteTaskId is null
            || operation.RemoteTaskId.Length > CharacterVoiceProfileOperation.MaximumCanonicalRemoteTaskIdLength)
        {
            throw new InvalidOperationException("remote_task_id_contract_mismatch");
        }

        // This route is activation-only. It never opens the WAV and never calls profile_prepare.
        return await ActivatePreparedOperationAsync(ownerId, operation.Id);
    }

    public async Task<CharacterVoiceProfileResponse?> CreateClonedAsync(
        Guid characterProfileId,
        string kind,
        string? sceneCode,
        string expectedTranscript,
        Stream referenceAudio,
        string referenceAudioFileName,
        Stream consentReceipt,
        string consentReceiptFileName,
        bool rightsAttested,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var parsedKind = ParseKind(kind);
        var character = await LoadCharacterProfileAsync(ownerId, characterProfileId, cancellationToken);
        if (character is null)
        {
            return null;
        }

        await EnsureNoDuplicateAsync(ownerId, characterProfileId, parsedKind, sceneCode, cancellationToken);
        await EnsureNoActiveOperationAsync(
            ownerId,
            characterProfileId,
            parsedKind,
            sceneCode,
            cancellationToken);
        var operation = await StageOperationAsync(
            ownerId,
            characterProfileId,
            oldProfile: null,
            parsedKind,
            sceneCode,
            expectedTranscript,
            referenceAudio,
            referenceAudioFileName,
            consentReceipt,
            consentReceiptFileName,
            rightsAttested,
            cancellationToken);
        return await PrepareAndActivateAsync(ownerId, character, operation);
    }

    public async Task<CharacterVoiceProfileResponse?> ReplaceDesignedWithCloneAsync(
        Guid characterProfileId,
        Guid profileId,
        string expectedTranscript,
        Stream referenceAudio,
        string referenceAudioFileName,
        Stream consentReceipt,
        string consentReceiptFileName,
        bool rightsAttested,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var existing = await LoadProfileAsync(ownerId, characterProfileId, profileId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        EnsureReplaceableBaseDesign(existing);
        var character = await LoadCharacterProfileAsync(ownerId, characterProfileId, cancellationToken)
            ?? throw new InvalidOperationException("找不到這個聲線所屬的角色。");
        await EnsureNoActiveOperationAsync(
            ownerId,
            characterProfileId,
            existing.Kind,
            existing.SceneCode,
            cancellationToken);
        var operation = await StageOperationAsync(
            ownerId,
            characterProfileId,
            existing,
            existing.Kind,
            existing.SceneCode,
            expectedTranscript,
            referenceAudio,
            referenceAudioFileName,
            consentReceipt,
            consentReceiptFileName,
            rightsAttested,
            cancellationToken);
        return await PrepareAndActivateAsync(ownerId, character, operation);
    }

    public async Task<CharacterVoiceProfileResponse?> CreateDesignedAsync(
        Guid characterProfileId,
        string kind,
        string? sceneCode,
        CreateDesignedVoiceProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ThreeWaSynthesisCapabilities.SupportsVoiceDesign)
        {
            throw new InvalidOperationException(
                ThreeWaSynthesisCapabilities.DesignVoiceUnavailableMessage);
        }

        var ownerId = EnsureCurrentOwnerId();
        var parsedKind = ParseKind(kind);
        var character = await LoadCharacterProfileAsync(ownerId, characterProfileId, cancellationToken);
        if (character is null)
        {
            return null;
        }

        await EnsureNoDuplicateAsync(ownerId, characterProfileId, parsedKind, sceneCode, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var profile = CharacterVoiceProfile.CreateDesign(
            Guid.NewGuid(),
            ownerId,
            characterProfileId,
            parsedKind,
            sceneCode,
            request.VoicePrompt,
            now);

        dbContext.CharacterVoiceProfiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(profile);
    }

    public async Task<CharacterVoiceProfileResponse?> RefreshStatusAsync(
        Guid characterProfileId,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var profile = await LoadProfileAsync(ownerId, characterProfileId, profileId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        if (profile.Mode != CharacterVoiceProfileMode.Clone
            || profile.Status is CharacterVoiceProfileStatus.Ready or CharacterVoiceProfileStatus.Failed)
        {
            return ToResponse(profile);
        }

        var status = await threeWaClient.GetStatusAsync(profile.VoiceProfileTaskId!, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(status.TaskStatus, "failed", StringComparison.OrdinalIgnoreCase) || status.TranscriptionFailed)
        {
            profile.MarkFailed(now);
        }
        else if (status.TranscriptConfirmed)
        {
            var confirmationIntent = profile.ConfirmationTranscriptIntent;
            if (confirmationIntent is null)
            {
                // Provider state is never allowed to invent the human confirmation intent. This
                // includes legacy rows: an operator must explicitly confirm a final transcript.
                throw new InvalidOperationException("confirmation_intent_missing");
            }

            if (!string.IsNullOrWhiteSpace(status.DraftTranscript))
            {
                string normalizedProviderTranscript;
                try
                {
                    normalizedProviderTranscript = StoryVoice.Domain.Series.SeriesValueValidator.NormalizePrintable(
                        status.DraftTranscript,
                        2_000,
                        nameof(status.DraftTranscript));
                }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException("confirmation_intent_mismatch");
                }

                if (!string.Equals(
                        confirmationIntent,
                        normalizedProviderTranscript,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("confirmation_intent_mismatch");
                }
            }

            profile.CompleteTranscriptConfirmation(now);
        }
        else if (profile.ConfirmationTranscriptIntent is null
            && !string.IsNullOrWhiteSpace(status.DraftTranscript))
        {
            profile.AttachDraftTranscript(profile.VoiceProfileTaskId!, status.DraftTranscript, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(profile);
    }

    public async Task<CharacterVoiceProfileResponse?> ConfirmTranscriptAsync(
        Guid characterProfileId,
        Guid profileId,
        ConfirmVoiceProfileTranscriptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ownerId = EnsureCurrentOwnerId();
        var profile = await LoadProfileAsync(ownerId, characterProfileId, profileId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        // Persist the exact intended final transcript before mutating the provider. A remote
        // success followed by a local failure can then be reconciled by refresh-status without
        // guessing from the mutable ASR draft.
        profile.StageTranscriptConfirmation(request.Transcript, DateTimeOffset.UtcNow);
        var intendedTranscript = profile.ConfirmationTranscriptIntent!;
        try
        {
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            profile = await LoadProfileAsync(
                ownerId,
                characterProfileId,
                profileId,
                CancellationToken.None);
            if (profile?.Status != CharacterVoiceProfileStatus.AwaitingTranscriptConfirmation
                || profile.ConfirmationTranscriptIntent != intendedTranscript)
            {
                throw new InvalidOperationException(
                    "逐字稿確認意圖的本地提交結果不明；未能證明持久化前不會呼叫 3wa。");
            }
        }

        // Durable path：不能被呼叫端斷線取消，但也不能無上限等待——
        // 3wa 若在回應 body 中途停住，沒有 token 保護會永遠掛住這個要求。
        using (var remoteConfirmTimeout = new CancellationTokenSource(DurableRemoteCallTimeout))
        {
            await threeWaClient.ConfirmAsync(
                profile.VoiceProfileTaskId!,
                intendedTranscript,
                remoteConfirmTimeout.Token);
        }

        profile.CompleteTranscriptConfirmation(DateTimeOffset.UtcNow);
        try
        {
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return ToResponse(profile);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            var persisted = await LoadProfileAsync(
                ownerId,
                characterProfileId,
                profileId,
                CancellationToken.None);
            if (persisted?.Status == CharacterVoiceProfileStatus.Ready
                && persisted.Transcript == intendedTranscript
                && persisted.ConfirmationTranscriptIntent == intendedTranscript)
            {
                return ToResponse(persisted);
            }

            throw new InvalidOperationException(
                "3wa 已接受逐字稿確認，但本地完成提交尚未能證明；確認意圖已保留，可用狀態重新對帳。");
        }
    }

    public async Task<CharacterVoiceProfileResponse?> RebuildAsync(
        Guid characterProfileId,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var profile = await LoadProfileAsync(ownerId, characterProfileId, profileId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        throw new InvalidOperationException(
            "克隆聲線重建目前已安全停用；在舊、新遠端 task 都能由 durable operation 追蹤前不會呼叫 3wa。");
    }

    public async Task<bool> DeleteAsync(Guid characterProfileId, Guid profileId, CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        await using var mutationLease = await CharacterVoiceMutationCoordinator.AcquireAsync(
            ownerId,
            characterProfileId,
            cancellationToken);
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        var character = await LockCharacterForVoiceMutationAsync(
            ownerId,
            characterProfileId,
            cancellationToken);
        if (character is null)
        {
            return false;
        }

        var profile = await LoadProfileAsync(ownerId, characterProfileId, profileId, cancellationToken);
        if (profile is null)
        {
            return false;
        }

        if (profile.Mode == CharacterVoiceProfileMode.Clone)
        {
            throw new InvalidOperationException(
                "克隆聲線刪除目前已安全停用；在遠端刪除可持久化確認前會保留授權、task 與 WAV 稽核資料。");
        }

        var hasActiveReplacement = await dbContext.CharacterVoiceProfileOperations.AnyAsync(
            operation => operation.OwnerId == ownerId
                && operation.CharacterProfileId == characterProfileId
                && operation.OldProfileId == profileId
                && (operation.State == CharacterVoiceProfileOperationState.Staged
                    || operation.State == CharacterVoiceProfileOperationState.RemotePrepared
                    || operation.State == CharacterVoiceProfileOperationState.NeedsAttention),
            cancellationToken);
        if (hasActiveReplacement)
        {
            throw new InvalidOperationException(
                "這組文字設計聲線已有待處理的 Clone replacement；完成或人工處理前禁止刪除。");
        }

        dbContext.CharacterVoiceProfiles.Remove(profile);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        if (profile.ReferenceAudioRelativePath is not null)
        {
            await audioStorage.DeleteAsync(profile.ReferenceAudioRelativePath, cancellationToken);
        }

        return true;
    }

    public async Task<CharacterVoiceProfileAudio?> GetReferenceAudioAsync(
        Guid characterProfileId,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var profile = await LoadProfileAsync(ownerId, characterProfileId, profileId, cancellationToken);
        if (profile?.ReferenceAudioRelativePath is null)
        {
            return null;
        }

        var fullPath = audioStorage.ResolveFullPath(profile.ReferenceAudioRelativePath);
        // 服務前驗證整檔雜湊（≤10 MiB，成本可接受）：把磁碟上被改動的檔案
        // 當成「已同意的錄音」播放給擁有者是完整性錯誤，不是 404。
        try
        {
            if (profile.ReferenceAudioSha256 is not null)
            {
                var actualSha256 = await ComputeFileSha256Async(fullPath, cancellationToken);
                if (!string.Equals(actualSha256, profile.ReferenceAudioSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "參考音檔與入庫時的雜湊不符，已停止提供播放；請聯絡管理員檢查儲存空間。");
                }
            }
            else if (!File.Exists(fullPath))
            {
                return null;
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        return new CharacterVoiceProfileAudio(fullPath, "audio/wav");
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<CharacterProfile?> LoadCharacterProfileAsync(
        Guid ownerId,
        Guid characterProfileId,
        CancellationToken cancellationToken) =>
        await dbContext.CharacterProfiles.SingleOrDefaultAsync(
            character => character.OwnerId == ownerId && character.Id == characterProfileId,
            cancellationToken);

    private async Task<CharacterProfile?> LockCharacterForVoiceMutationAsync(
        Guid ownerId,
        Guid characterProfileId,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return await LoadCharacterProfileAsync(ownerId, characterProfileId, cancellationToken);
        }

        return await dbContext.CharacterProfiles
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM character_profiles
                WHERE "OwnerId" = {ownerId} AND "Id" = {characterProfileId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<CharacterVoiceProfile?> LoadProfileAsync(
        Guid ownerId,
        Guid characterProfileId,
        Guid profileId,
        CancellationToken cancellationToken) =>
        await dbContext.CharacterVoiceProfiles.SingleOrDefaultAsync(
            profile => profile.OwnerId == ownerId
                && profile.CharacterProfileId == characterProfileId
                && profile.Id == profileId,
            cancellationToken);

    private async Task<CharacterVoiceProfile?> LoadOperationProfileAsync(
        CharacterVoiceProfileOperation operation,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.CharacterVoiceProfiles.SingleOrDefaultAsync(
            candidate => candidate.OwnerId == operation.OwnerId
                && candidate.CharacterProfileId == operation.CharacterProfileId
                && candidate.Id == operation.NewProfileId,
            cancellationToken);
        return profile is not null
            && profile.Mode == CharacterVoiceProfileMode.Clone
            && profile.VoiceProfileTaskId == operation.RemoteTaskId
            && profile.ReferenceAudioRelativePath == operation.ReferenceAudioRelativePath
                ? profile
                : null;
    }

    private async Task EnsureNoDuplicateAsync(
        Guid ownerId,
        Guid characterProfileId,
        CharacterVoiceProfileKind kind,
        string? sceneCode,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.CharacterVoiceProfiles.AnyAsync(
            profile => profile.OwnerId == ownerId
                && profile.CharacterProfileId == characterProfileId
                && (kind == CharacterVoiceProfileKind.Base
                    ? profile.Kind == CharacterVoiceProfileKind.Base
                    : profile.SceneCode == sceneCode),
            cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException(
                kind == CharacterVoiceProfileKind.Base
                    ? "這個角色已經有基礎聲線了。"
                    : "這個角色在這個情境下已經有聲線了。");
        }
    }

    private async Task<CharacterVoiceProfileOperation> StageOperationAsync(
        Guid ownerId,
        Guid characterProfileId,
        CharacterVoiceProfile? oldProfile,
        CharacterVoiceProfileKind kind,
        string? sceneCode,
        string expectedTranscript,
        Stream referenceAudio,
        string referenceAudioFileName,
        Stream consentReceipt,
        string consentReceiptFileName,
        bool rightsAttested,
        CancellationToken cancellationToken)
    {
        var canonicalExpectedTranscript = CharacterVoiceTranscriptCanonicalizer.Normalize(
            expectedTranscript);
        var stored = await audioStorage.SaveAsync(referenceAudio, referenceAudioFileName, cancellationToken);
        CharacterVoiceProfileOperation? operation = null;
        var preserveStoredAudio = false;
        try
        {
            var validated = await ReferenceWavValidator.ValidateAsync(
                audioStorage.ResolveFullPath(stored.RelativePath),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var consentEvidence = await CloneConsentReceiptValidator.ValidateAsync(
                consentReceipt,
                consentReceiptFileName,
                rightsAttested,
                stored.Sha256Hex,
                canonicalExpectedTranscript,
                now,
                cancellationToken);

            await using var mutationLease = await CharacterVoiceMutationCoordinator.AcquireAsync(
                ownerId,
                characterProfileId,
                cancellationToken);
            await using var transaction = dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;
            var lockedCharacter = await LockCharacterForVoiceMutationAsync(
                ownerId,
                characterProfileId,
                cancellationToken);
            if (lockedCharacter is null)
            {
                throw new InvalidOperationException("角色已被刪除；Clone operation 未送出至 3wa。");
            }

            if (oldProfile is null)
            {
                await EnsureNoDuplicateAsync(
                    ownerId,
                    characterProfileId,
                    kind,
                    sceneCode,
                    cancellationToken);
            }
            else
            {
                var currentOldProfile = await LoadProfileAsync(
                    ownerId,
                    characterProfileId,
                    oldProfile.Id,
                    cancellationToken);
                if (currentOldProfile is null
                    || currentOldProfile.ConcurrencyStamp != oldProfile.ConcurrencyStamp)
                {
                    throw new InvalidOperationException(
                        "要取代的文字設計聲線已經變更；Clone operation 未送出至 3wa。");
                }

                EnsureReplaceableBaseDesign(currentOldProfile);
                oldProfile = currentOldProfile;
            }

            await EnsureNoActiveOperationAsync(
                ownerId,
                characterProfileId,
                kind,
                sceneCode,
                cancellationToken);
            operation = oldProfile is null
                ? CharacterVoiceProfileOperation.StageCreate(
                    Guid.NewGuid(),
                    ownerId,
                    characterProfileId,
                    Guid.NewGuid(),
                    kind,
                    sceneCode,
                    consentEvidence,
                    canonicalExpectedTranscript,
                    stored.RelativePath,
                    stored.Sha256Hex,
                    validated.DurationSeconds,
                    ownerId,
                    BuildCredentialKeyId(),
                    now)
                : CharacterVoiceProfileOperation.StageReplacement(
                    Guid.NewGuid(),
                    ownerId,
                    characterProfileId,
                    oldProfile.Id,
                    oldProfile.ConcurrencyStamp,
                    Guid.NewGuid(),
                    consentEvidence,
                    canonicalExpectedTranscript,
                    stored.RelativePath,
                    stored.Sha256Hex,
                    validated.DurationSeconds,
                    ownerId,
                    BuildCredentialKeyId(),
                    now);

            dbContext.CharacterVoiceProfileOperations.Add(operation);
            try
            {
                // From this durable stage onward no work is tied to RequestAborted.
                await dbContext.SaveChangesAsync(CancellationToken.None);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(CancellationToken.None);
                }

                preserveStoredAudio = true;
                return operation;
            }
            catch
            {
                if (transaction is not null)
                {
                    try
                    {
                        await transaction.DisposeAsync();
                    }
                    catch
                    {
                        // A failed transaction cleanup leaves the commit outcome unknown. Keep the
                        // evidence file and continue trying the deterministic operation id.
                        preserveStoredAudio = true;
                    }
                }

                dbContext.ChangeTracker.Clear();
                try
                {
                    var persisted = await dbContext.CharacterVoiceProfileOperations
                        .SingleOrDefaultAsync(
                            candidate => candidate.OwnerId == ownerId && candidate.Id == operation.Id,
                            CancellationToken.None);
                    if (persisted is not null)
                    {
                        preserveStoredAudio = true;
                        return persisted;
                    }
                }
                catch
                {
                    // The stage commit outcome cannot be proven while storage is unavailable.
                    // Preserve the WAV rather than deleting potential durable evidence.
                    preserveStoredAudio = true;
                }

                throw;
            }
        }
        catch
        {
            if (!preserveStoredAudio)
            {
                await audioStorage.DeleteAsync(stored.RelativePath, CancellationToken.None);
            }

            throw;
        }
    }

    private async Task<CharacterVoiceProfileResponse> PrepareAndActivateAsync(
        Guid ownerId,
        CharacterProfile character,
        CharacterVoiceProfileOperation stagedOperation)
    {
        // 上傳前先驗證本地 WAV 與入庫時的 canonical 雜湊一致——這是唯一會把
        // 檔案內容送去克隆的地方；損毀或被調包的音檔絕不能被當成已同意的錄音送出。
        var referenceAudioFullPath = audioStorage.ResolveFullPath(
            stagedOperation.ReferenceAudioRelativePath);
        var actualReferenceSha256 = await ComputeFileSha256Async(referenceAudioFullPath);
        if (!string.Equals(
            actualReferenceSha256,
            stagedOperation.ReferenceAudioSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            await MarkRejectedAsync(
                ownerId,
                stagedOperation.Id,
                "reference_audio_integrity_mismatch");
            throw new InvalidOperationException(
                "本地保存的參考音檔與入庫時的雜湊不符，已中止上傳；請重新上傳原始錄音。");
        }

        VoiceProfilePrepareResult providerProfile;
        try
        {
            await using var uploadStream = File.OpenRead(referenceAudioFullPath);
            // Durable path：同上，用有限逾時取代 CancellationToken.None，
            // 避免 3wa 停在 body 中途時永遠佔住要求與 DbContext。
            using var remotePrepareTimeout = new CancellationTokenSource(DurableRemoteCallTimeout);
            providerProfile = await threeWaClient.PrepareAsync(
                uploadStream,
                $"{stagedOperation.NewProfileId:N}.wav",
                BuildProfileName(character.CanonicalName, stagedOperation.Kind, stagedOperation.SceneCode),
                stagedOperation.ConsentType,
                stagedOperation.ExpectedTranscript,
                remotePrepareTimeout.Token);
        }
        catch (ThreeWaAiHubException exception) when (
            exception.FailureKind is ThreeWaAiHubFailureKind.PreSend
                or ThreeWaAiHubFailureKind.RemoteAuthenticationRejected)
        {
            await MarkRejectedAsync(
                ownerId,
                stagedOperation.Id,
                exception.FailureKind == ThreeWaAiHubFailureKind.PreSend
                    ? "remote_prepare_not_sent"
                    : "remote_prepare_auth_rejected");
            throw;
        }
        catch
        {
            await MarkNeedsAttentionAsync(
                ownerId,
                stagedOperation.Id,
                "remote_prepare_uncertain");
            throw;
        }

        var normalizedDraft = NormalizeProviderDraftForStorage(providerProfile.DraftTranscript);
        CharacterVoiceProfileOperation operation;
        try
        {
            operation = await PersistRemotePreparedAsync(
                ownerId,
                stagedOperation.Id,
                providerProfile.TaskId,
                normalizedDraft.Value);
        }
        catch
        {
            await MarkNeedsAttentionAsync(
                ownerId,
                stagedOperation.Id,
                "remote_task_persistence_uncertain");
            throw;
        }

        if (normalizedDraft.IsInvalid)
        {
            await MarkNeedsAttentionAsync(
                ownerId,
                operation.Id,
                "remote_draft_contract_mismatch");
            throw new InvalidOperationException(
                "3wa 回傳的辨識草稿不符合儲存合約；已先保留 operation、task 與 WAV 等待人工處理。");
        }

        if (providerProfile.TaskId.Length
            > CharacterVoiceProfileOperation.MaximumCanonicalRemoteTaskIdLength)
        {
            await MarkNeedsAttentionAsync(
                ownerId,
                operation.Id,
                "remote_task_id_contract_mismatch");
            throw new InvalidOperationException(
                "3wa 回傳的 clone task handle 不符合目前 canonical 合約；已保留 operation、task 與 WAV 等待人工處理。");
        }

        return await ActivatePreparedOperationAsync(ownerId, operation.Id);
    }

    private async Task<CharacterVoiceProfileOperation> PersistRemotePreparedAsync(
        Guid ownerId,
        Guid operationId,
        string remoteTaskId,
        string? asrDraftTranscript)
    {
        CharacterVoiceProfileOperation? operation = null;
        try
        {
            operation = await dbContext.CharacterVoiceProfileOperations.SingleOrDefaultAsync(
                candidate => candidate.OwnerId == ownerId && candidate.Id == operationId,
                CancellationToken.None)
                ?? throw new InvalidOperationException("找不到已暫存的克隆 operation。");
            operation.MarkRemotePrepared(
                remoteTaskId,
                asrDraftTranscript,
                DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return operation;
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            var persisted = await dbContext.CharacterVoiceProfileOperations.SingleOrDefaultAsync(
                candidate => candidate.OwnerId == ownerId && candidate.Id == operationId,
                CancellationToken.None);
            if (persisted is not null
                && persisted.RemoteTaskId == remoteTaskId
                && persisted.State == CharacterVoiceProfileOperationState.RemotePrepared)
            {
                return persisted;
            }

            if (persisted is not null && persisted.State == CharacterVoiceProfileOperationState.Staged)
            {
                persisted.MarkRemotePrepared(
                    remoteTaskId,
                    asrDraftTranscript,
                    DateTimeOffset.UtcNow);
                await dbContext.SaveChangesAsync(CancellationToken.None);
                return persisted;
            }

            throw new InvalidOperationException(
                "3wa clone task 已建立，但 task handle 無法持久化；WAV 與 operation 已保留，禁止自動清除。");
        }
    }

    private async Task<CharacterVoiceProfileResponse> ActivatePreparedOperationAsync(
        Guid ownerId,
        Guid operationId)
    {
        try
        {
            dbContext.ChangeTracker.Clear();
            var operation = await dbContext.CharacterVoiceProfileOperations.SingleOrDefaultAsync(
                candidate => candidate.OwnerId == ownerId && candidate.Id == operationId,
                CancellationToken.None)
                ?? throw new InvalidOperationException("找不到已準備完成的克隆 operation。");
            if (operation.State != CharacterVoiceProfileOperationState.RemotePrepared
                || operation.RemoteTaskId is null)
            {
                throw new InvalidOperationException("克隆 operation 尚未持久化遠端 task，禁止啟用。");
            }

            var profile = BuildCloneProfile(operation);
            if (operation.Type == CharacterVoiceProfileOperationType.Replace
                && dbContext.Database.IsRelational())
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    CancellationToken.None);
                await ReplaceWithinTransactionAsync(operation, profile);
                await transaction.CommitAsync(CancellationToken.None);
            }
            else if (operation.Type == CharacterVoiceProfileOperationType.Replace)
            {
                var current = await LoadReplacementTargetAsync(operation);
                dbContext.CharacterVoiceProfiles.Remove(current);
                dbContext.CharacterVoiceProfiles.Add(profile);
                operation.MarkActivated(DateTimeOffset.UtcNow);
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }
            else
            {
                dbContext.CharacterVoiceProfiles.Add(profile);
                operation.MarkActivated(DateTimeOffset.UtcNow);
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            return ToResponse(profile);
        }
        catch
        {
            var resolved = await ResolveActivationOutcomeAsync(ownerId, operationId);
            if (resolved is not null)
            {
                return resolved;
            }

            await MarkNeedsAttentionAsync(ownerId, operationId, "local_activation_uncertain");
            throw;
        }
    }

    private async Task ReplaceWithinTransactionAsync(
        CharacterVoiceProfileOperation operation,
        CharacterVoiceProfile replacement)
    {
        var current = await LoadReplacementTargetAsync(operation);

        dbContext.CharacterVoiceProfiles.Remove(current);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        // PostgreSQL's filtered base-slot unique index is immediate (not deferrable). Persist the
        // delete first, then the insert, while the caller's transaction still makes both atomic.
        dbContext.CharacterVoiceProfiles.Add(replacement);
        operation.MarkActivated(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<CharacterVoiceProfile> LoadReplacementTargetAsync(
        CharacterVoiceProfileOperation operation)
    {
        var current = await dbContext.CharacterVoiceProfiles.SingleOrDefaultAsync(
            profile => profile.OwnerId == operation.OwnerId
                && profile.CharacterProfileId == operation.CharacterProfileId
                && profile.Id == operation.OldProfileId,
            CancellationToken.None)
            ?? throw new InvalidOperationException("要取代的文字設計聲線已經變更，請重新整理後再試。");
        EnsureReplaceableBaseDesign(current);
        if (current.ConcurrencyStamp != operation.OldProfileConcurrencyStamp)
        {
            throw new InvalidOperationException("要取代的文字設計聲線已被更新，禁止啟用舊 operation。");
        }

        return current;
    }

    private async Task<CharacterVoiceProfileResponse?> ResolveActivationOutcomeAsync(
        Guid ownerId,
        Guid operationId)
    {
        try
        {
            dbContext.ChangeTracker.Clear();
            var operation = await dbContext.CharacterVoiceProfileOperations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.OwnerId == ownerId && candidate.Id == operationId,
                    CancellationToken.None);
            if (operation is null)
            {
                return null;
            }

            var profile = await dbContext.CharacterVoiceProfiles
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.OwnerId == ownerId
                        && candidate.CharacterProfileId == operation.CharacterProfileId
                        && candidate.Id == operation.NewProfileId,
                    CancellationToken.None);
            return profile is not null
                && profile.Mode == CharacterVoiceProfileMode.Clone
                && profile.VoiceProfileTaskId == operation.RemoteTaskId
                && profile.ReferenceAudioRelativePath == operation.ReferenceAudioRelativePath
                    ? ToResponse(profile)
                    : null;
        }
        catch
        {
            // Unknown stays unknown. Never compensate or delete evidence based only on an exception.
            return null;
        }
    }

    private async Task MarkNeedsAttentionAsync(Guid ownerId, Guid operationId, string safeErrorCode)
    {
        try
        {
            dbContext.ChangeTracker.Clear();
            var operation = await dbContext.CharacterVoiceProfileOperations.SingleOrDefaultAsync(
                candidate => candidate.OwnerId == ownerId && candidate.Id == operationId,
                CancellationToken.None);
            if (operation is null || operation.State == CharacterVoiceProfileOperationState.Activated)
            {
                return;
            }

            operation.MarkNeedsAttention(safeErrorCode, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // Do not log task handles, transcripts, credential identifiers, or paths. The staged
            // database row and private WAV remain untouched for manual reconciliation.
        }
    }

    private async Task MarkRejectedAsync(Guid ownerId, Guid operationId, string safeErrorCode)
    {
        try
        {
            dbContext.ChangeTracker.Clear();
            var operation = await dbContext.CharacterVoiceProfileOperations.SingleOrDefaultAsync(
                candidate => candidate.OwnerId == ownerId && candidate.Id == operationId,
                CancellationToken.None);
            if (operation is null || operation.State != CharacterVoiceProfileOperationState.Staged)
            {
                return;
            }

            operation.MarkRejected(safeErrorCode, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // If the rejection marker cannot be proven, leave the durable Staged row and WAV in
            // place. The owner-scoped operation list exposes this as an interrupted/unknown stage.
        }
    }

    private static CharacterVoiceProfile BuildCloneProfile(CharacterVoiceProfileOperation operation)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = CharacterVoiceProfile.CreateClone(
            operation.NewProfileId,
            operation.OwnerId,
            operation.CharacterProfileId,
            operation.Kind,
            operation.SceneCode,
            operation.ConsentType,
            operation.ReferenceAudioRelativePath,
            operation.ReferenceAudioSha256,
            operation.ReferenceAudioDurationSeconds,
            operation.RightsConfirmedByUserId,
            now);
        profile.AttachExpectedTranscript(operation.ExpectedTranscript, now);
        if (operation.AsrDraftTranscript is not null)
        {
            profile.AttachDraftTranscript(operation.RemoteTaskId!, operation.AsrDraftTranscript, now);
        }
        else
        {
            profile.AttachPendingTask(operation.RemoteTaskId!, now);
        }

        return profile;
    }

    private async Task EnsureNoActiveOperationAsync(
        Guid ownerId,
        Guid characterProfileId,
        CharacterVoiceProfileKind kind,
        string? sceneCode,
        CancellationToken cancellationToken)
    {
        var slotKey = kind == CharacterVoiceProfileKind.Base ? "base" : $"scene:{sceneCode}";
        var exists = await dbContext.CharacterVoiceProfileOperations.AnyAsync(
            operation => operation.OwnerId == ownerId
                && operation.CharacterProfileId == characterProfileId
                && operation.SlotKey == slotKey
                && (operation.State == CharacterVoiceProfileOperationState.Staged
                    || operation.State == CharacterVoiceProfileOperationState.RemotePrepared
                    || operation.State == CharacterVoiceProfileOperationState.NeedsAttention),
            cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException(
                "這個聲線位置已有待處理的 Clone operation；人工確認前禁止再次送出錄音。");
        }
    }

    private string BuildCredentialKeyId()
    {
        if (!string.IsNullOrWhiteSpace(threeWaOptions.Value.CredentialKeyId))
        {
            return threeWaOptions.Value.CredentialKeyId.Trim();
        }

        var token = threeWaOptions.Value.ApiToken?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return "unconfigured";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return $"sha256:{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static NormalizedProviderDraft NormalizeProviderDraftForStorage(string? draftTranscript)
    {
        if (string.IsNullOrWhiteSpace(draftTranscript))
        {
            return new NormalizedProviderDraft(Value: null, IsInvalid: false);
        }

        try
        {
            var normalized = StoryVoice.Domain.Series.SeriesValueValidator.NormalizePrintable(
                draftTranscript,
                2_000,
                nameof(draftTranscript));
            return new NormalizedProviderDraft(normalized, IsInvalid: false);
        }
        catch (ArgumentException)
        {
            // The task handle is more important than an untrusted provider draft. Persist the
            // handle first, then leave the operation in NeedsAttention without activating it.
            return new NormalizedProviderDraft(Value: null, IsInvalid: true);
        }
    }

    private sealed record NormalizedProviderDraft(string? Value, bool IsInvalid);

    private static void EnsureReplaceableBaseDesign(CharacterVoiceProfile profile)
    {
        // 兩種可安全取代的情況：
        // 1) 文字設計基礎聲線（原本的規則，沒有遠端 task 需要顧慮）。
        // 2) 已標記 Failed 的克隆聲線——遠端 task 已由 3wa 回報失敗，槽位若不能
        //    取代就會永久卡死（rebuild 與 delete 都是刻意停用的）。原 operation
        //    列與 WAV 稽核資料照舊保留，只有 profile 列被替換。
        var isReplaceableDesign = profile.Kind == CharacterVoiceProfileKind.Base
            && profile.SceneCode is null
            && profile.Mode == CharacterVoiceProfileMode.Design;
        var isFailedClone = profile.Mode == CharacterVoiceProfileMode.Clone
            && profile.Status == CharacterVoiceProfileStatus.Failed;
        if (!isReplaceableDesign && !isFailedClone)
        {
            throw new InvalidOperationException("只有文字設計基礎聲線，或已失敗的克隆聲線，可以安全取代為新的錄音克隆。");
        }
    }

    internal static string BuildProfileName(
        string characterName,
        CharacterVoiceProfileKind kind,
        string? sceneCode)
    {
        var slot = kind == CharacterVoiceProfileKind.Base ? "base" : sceneCode!;
        var fullName = $"{characterName}-{slot}";
        if (fullName.Length <= 120)
        {
            return fullName;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullName)))
            .ToLowerInvariant()[..12];
        var suffix = $"-{slot}-{hash}";
        var prefixBudget = 120 - suffix.Length;
        var builder = new StringBuilder(prefixBudget);
        foreach (var rune in characterName.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > prefixBudget)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.Append(suffix).ToString();
    }

    private static CharacterVoiceProfileKind ParseKind(string kind)
    {
        if (!Enum.TryParse<CharacterVoiceProfileKind>(kind, ignoreCase: true, out var parsed))
        {
            throw new ArgumentException("聲線類型無效。", nameof(kind));
        }

        return parsed;
    }

    private static CharacterVoiceProfileResponse ToResponse(CharacterVoiceProfile profile) =>
        new(
            profile.Id,
            profile.CharacterProfileId,
            profile.Kind.ToString(),
            profile.SceneCode,
            profile.Mode.ToString(),
            profile.ConsentType,
            profile.VoicePromptText,
            profile.ExpectedTranscript,
            profile.AsrDraftTranscript,
            profile.ConfirmationTranscriptIntent,
            profile.Transcript,
            profile.TranscriptConfirmedAt is not null,
            profile.Status.ToString(),
            profile.ReferenceAudioDurationSeconds,
            profile.CreatedAt,
            profile.UpdatedAt);

    private static CharacterVoiceProfileOperationResponse ToOperationResponse(
        CharacterVoiceProfileOperation operation) =>
        new(
            operation.Id,
            operation.NewProfileId,
            operation.OldProfileId,
            operation.Type.ToString(),
            operation.State.ToString(),
            operation.Kind.ToString(),
            operation.SceneCode,
            operation.RemoteTaskId is not null,
            operation.State == CharacterVoiceProfileOperationState.Activated
                ? null
                : GetOperationAttentionCode(operation),
            HasCurrentConsentEvidence(operation) ? "attested" : "missing",
            operation.UsageScopes,
            operation.CreatedAt,
            operation.UpdatedAt,
            operation.RemotePreparedAt,
            operation.ActivatedAt);

    private static bool HasCurrentConsentEvidence(CharacterVoiceProfileOperation operation) =>
        operation.PrivateEvaluationAllowed
        && string.Equals(
            operation.EvidenceVersion,
            CharacterVoiceConsentEvidence.CurrentEvidenceVersion,
            StringComparison.Ordinal)
        && string.Equals(
            operation.AttestationVersion,
            CharacterVoiceConsentEvidence.CurrentAttestationVersion,
            StringComparison.Ordinal)
        && operation.ConsentRecordSha256.Length == 64
        && operation.ConsentReceiptSha256.Length == 64
        && operation.ExpectedTranscriptSha256.Length == 64;

    private static string GetOperationAttentionCode(CharacterVoiceProfileOperation operation) =>
        operation.SafeErrorCode
        ?? operation.State switch
        {
            CharacterVoiceProfileOperationState.Staged => "staged_or_prepare_outcome_unknown",
            CharacterVoiceProfileOperationState.RemotePrepared => "activation_pending",
            CharacterVoiceProfileOperationState.Rejected => "remote_prepare_rejected",
            _ => "operation_needs_attention",
        };

    private Guid EnsureCurrentOwnerId()
    {
        if (currentUser.UserId == Guid.Empty)
        {
            throw new InvalidOperationException("目前使用者識別碼無效。");
        }

        return currentUser.UserId;
    }

}

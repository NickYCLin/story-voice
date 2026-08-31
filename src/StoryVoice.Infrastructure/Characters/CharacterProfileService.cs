using System.Data;
using Microsoft.EntityFrameworkCore;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Characters;
using StoryVoice.Domain.Characters;
using StoryVoice.Infrastructure.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Infrastructure.Characters;

/// <summary>
/// Owner-scoped CRUD for the character library. Deleting a character cascades its
/// <see cref="StoryVoice.Domain.Narrations.CharacterVoiceProfile"/> rows at the database level
/// (<c>FK_cvp_character_profile ON DELETE CASCADE</c>) but not their reference-audio files on
/// disk, so this service cleans those up itself. A character still linked from any
/// <see cref="StoryVoice.Domain.Series.SeriesCharacter"/> can't be deleted
/// (<c>FK_series_characters_character_profile ON DELETE RESTRICT</c>) — the series has to unlink
/// it first.
/// </summary>
internal sealed class CharacterProfileService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser,
    LocalCharacterAvatarStorage avatarStorage,
    LocalCharacterVoiceAudioStorage voiceAudioStorage) : ICharacterProfileService
{
    public async Task<IReadOnlyList<CharacterProfileResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var profiles = await dbContext.CharacterProfiles
            .AsNoTracking()
            .Where(profile => profile.OwnerId == ownerId)
            .OrderBy(profile => profile.CanonicalName)
            .ToListAsync(cancellationToken);
        return profiles.Select(ToResponse).ToArray();
    }

    public async Task<CharacterProfileResponse?> GetAsync(Guid characterProfileId, CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var profile = await LoadAsync(ownerId, characterProfileId, cancellationToken);
        return profile is null ? null : ToResponse(profile);
    }

    public async Task<CharacterProfileResponse> CreateAsync(
        CreateCharacterProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ownerId = EnsureCurrentOwnerId();
        var now = DateTimeOffset.UtcNow;
        var profile = CharacterProfile.Create(
            Guid.NewGuid(),
            ownerId,
            request.CanonicalName,
            avatarRelativePath: null,
            request.Age,
            request.Gender,
            request.Birthday,
            request.Personality,
            request.Catchphrase,
            request.Background,
            request.SpeakingStyle,
            now);

        dbContext.CharacterProfiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(profile);
    }

    public async Task<CharacterProfileResponse?> UpdateAsync(
        Guid characterProfileId,
        UpdateCharacterProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ownerId = EnsureCurrentOwnerId();
        var profile = await LoadAsync(ownerId, characterProfileId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        profile.Update(
            request.CanonicalName,
            request.Age,
            request.Gender,
            request.Birthday,
            request.Personality,
            request.Catchphrase,
            request.Background,
            request.SpeakingStyle,
            DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(profile);
    }

    public async Task<CharacterProfileResponse?> SetAvatarAsync(
        Guid characterProfileId,
        Stream avatar,
        string fileName,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var profile = await LoadAsync(ownerId, characterProfileId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var previousAvatarPath = profile.AvatarRelativePath;
        var (relativePath, _) = await avatarStorage.SaveAsync(avatar, fileName, cancellationToken);
        profile.SetAvatar(relativePath, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (previousAvatarPath is not null)
        {
            await avatarStorage.DeleteAsync(previousAvatarPath, cancellationToken);
        }

        return ToResponse(profile);
    }

    public async Task<bool> DeleteAsync(Guid characterProfileId, CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        await using var mutationLease = await CharacterVoiceMutationCoordinator.AcquireAsync(
            ownerId,
            characterProfileId,
            cancellationToken);
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        var profile = await LockForVoiceMutationAsync(ownerId, characterProfileId, cancellationToken);
        if (profile is null)
        {
            return false;
        }

        var stillLinkedToASeries = await dbContext.SeriesCharacters.AnyAsync(
            character => character.OwnerId == ownerId && character.CharacterProfileId == characterProfileId,
            cancellationToken);
        if (stillLinkedToASeries)
        {
            throw new InvalidOperationException("這個角色目前還被某些系列使用中，請先從系列移除這個角色後再刪除。");
        }

        var hasCloneOrCloneOperation = await dbContext.CharacterVoiceProfiles.AnyAsync(
                voiceProfile => voiceProfile.OwnerId == ownerId
                    && voiceProfile.CharacterProfileId == characterProfileId
                    && voiceProfile.Mode == StoryVoice.Domain.Narrations.CharacterVoiceProfileMode.Clone,
                cancellationToken)
            || await dbContext.CharacterVoiceProfileOperations.AnyAsync(
                operation => operation.OwnerId == ownerId
                    && operation.CharacterProfileId == characterProfileId
                    // Rejected 依領域定義證明沒有建立任何遠端 task，只是稽核紀錄，
                    // 不應永久封鎖整個角色的刪除。
                    && operation.State != StoryVoice.Domain.Narrations.CharacterVoiceProfileOperationState.Rejected,
                cancellationToken);
        if (hasCloneOrCloneOperation)
        {
            throw new InvalidOperationException(
                "這個角色含有 Clone 或待處理的 Clone operation；遠端刪除尚未能持久化確認前禁止刪除整個角色。");
        }

        var voiceAudioPaths = await dbContext.CharacterVoiceProfiles
            .Where(voiceProfile => voiceProfile.OwnerId == ownerId && voiceProfile.CharacterProfileId == characterProfileId)
            .Select(voiceProfile => voiceProfile.ReferenceAudioRelativePath)
            .Where(path => path != null)
            .ToListAsync(cancellationToken);

        try
        {
            dbContext.CharacterProfiles.Remove(profile);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException("這個角色目前還被某些系列使用中，請先從系列移除這個角色後再刪除。", exception);
        }

        if (profile.AvatarRelativePath is not null)
        {
            await avatarStorage.DeleteAsync(profile.AvatarRelativePath, cancellationToken);
        }

        foreach (var path in voiceAudioPaths)
        {
            await voiceAudioStorage.DeleteAsync(path!, cancellationToken);
        }

        return true;
    }

    private async Task<CharacterProfile?> LockForVoiceMutationAsync(
        Guid ownerId,
        Guid characterProfileId,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return await LoadAsync(ownerId, characterProfileId, cancellationToken);
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

    public async Task<CharacterProfileAvatar?> GetAvatarAsync(Guid characterProfileId, CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var profile = await LoadAsync(ownerId, characterProfileId, cancellationToken);
        if (profile?.AvatarRelativePath is null)
        {
            return null;
        }

        return new CharacterProfileAvatar(
            avatarStorage.ResolveFullPath(profile.AvatarRelativePath),
            avatarStorage.ResolveContentType(profile.AvatarRelativePath));
    }

    public async Task<CharacterProfileResponse?> SetActiveAsync(
        Guid characterProfileId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var profile = await LoadAsync(ownerId, characterProfileId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (isActive)
        {
            profile.Activate(now);
        }
        else
        {
            profile.Deactivate(now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(profile);
    }

    public Task<GeneratedCharacterProfileAssistResponse> GenerateAssistAsync(
        GenerateCharacterProfileAssistRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = string.IsNullOrWhiteSpace(request.CanonicalName) ? "該角色" : request.CanonicalName.Trim();
        var gender = request.Gender?.Trim();
        var field = request.FieldToGenerate?.Trim().ToLowerInvariant() ?? "all";

        var isFemale = gender is "女" or "female" or "Female";

        string? personality = null;
        string? background = null;
        string? speakingStyle = null;
        string? catchphrase = null;

        if (field is "all" or "personality")
        {
            personality = string.IsNullOrWhiteSpace(request.ExistingPersonality)
                ? $"{name}性格沉著冷靜，心思縝密，對周遭人事觀察細緻入微。在關鍵時刻表現果決，內心情感深沉而不輕易外露。"
                : request.ExistingPersonality;
        }

        if (field is "all" or "background")
        {
            background = string.IsNullOrWhiteSpace(request.ExistingBackground)
                ? $"{name}自幼經歷豐富，閱歷寬廣，曾走訪多方並累積了深厚的見聞。在故事中承擔著推動情節發展的重要使命與守護責任。"
                : request.ExistingBackground;
        }

        if (field is "all" or "speakingStyle")
        {
            speakingStyle = string.IsNullOrWhiteSpace(request.ExistingSpeakingStyle)
                ? $"{name}說話語氣沉穩溫和，條理分明，語速不徐不疾，習慣在關鍵陳述後稍作停頓以引人深思。"
                : request.ExistingSpeakingStyle;
        }

        if (field is "all" or "catchphrase")
        {
            catchphrase = string.IsNullOrWhiteSpace(request.ExistingCatchphrase)
                ? (isFemale ? "「事情總會有轉機的。」" : "「真相往往藏在細節之中。」")
                : request.ExistingCatchphrase;
        }

        return Task.FromResult(new GeneratedCharacterProfileAssistResponse(
            personality,
            background,
            speakingStyle,
            catchphrase));
    }

    private async Task<CharacterProfile?> LoadAsync(
        Guid ownerId,
        Guid characterProfileId,
        CancellationToken cancellationToken) =>
        await dbContext.CharacterProfiles.SingleOrDefaultAsync(
            profile => profile.OwnerId == ownerId && profile.Id == characterProfileId,
            cancellationToken);

    private static CharacterProfileResponse ToResponse(CharacterProfile profile) =>
        new(
            profile.Id,
            profile.CanonicalName,
            profile.AvatarRelativePath is not null,
            profile.Age,
            profile.Gender,
            profile.Birthday,
            profile.Personality,
            profile.Catchphrase,
            profile.Background,
            profile.SpeakingStyle,
            profile.IsActive,
            profile.CreatedAt,
            profile.UpdatedAt);

    private Guid EnsureCurrentOwnerId()
    {
        if (currentUser.UserId == Guid.Empty)
        {
            throw new InvalidOperationException("目前使用者識別碼無效。");
        }

        return currentUser.UserId;
    }
}

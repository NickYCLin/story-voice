using Microsoft.EntityFrameworkCore;
using StoryVoice.Domain.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

/// <summary>Owner-scoped, explicitly selected character-management voices; never a provider catalog fallback.</summary>
public static class ManagedNarrationVoices
{
    public const string Prefix = "localclone:";
    public const string Error = "請從角色管理選擇已啟用、Ready 且授權有效的克隆聲線；不支援 Edge 或自動替代聲線。";
    public static string Reference(Guid profileId) => $"{Prefix}{profileId:N}";
    public static Guid Parse(string provider, string voice)
    {
        if (provider != CharacterVoiceProviders.LocalClone || voice is null || !voice.StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(voice[Prefix.Length..], "N", out var id) || id == Guid.Empty)
            throw new ArgumentException(Error);
        return id;
    }

    public static IQueryable<CharacterVoiceProfile> Eligible(StoryVoiceDbContext db, Guid ownerId) =>
        db.CharacterVoiceProfiles.AsNoTracking().Where(p => p.OwnerId == ownerId
            && p.Kind == CharacterVoiceProfileKind.Base && p.Mode == CharacterVoiceProfileMode.Clone
            && p.Status == CharacterVoiceProfileStatus.Ready && p.ReferenceAudioRelativePath != null && p.ReferenceAudioRelativePath != ""
            && p.ReferenceAudioSha256 != null && p.ReferenceAudioSha256 != "" && p.Transcript != null && p.Transcript != ""
            && db.CharacterProfiles.Any(c => c.Id == p.CharacterProfileId && c.OwnerId == ownerId && c.IsActive)
            && db.CharacterVoiceProfileOperations.Any(o => o.OwnerId == ownerId && o.NewProfileId == p.Id
                && o.CharacterProfileId == p.CharacterProfileId && o.State == CharacterVoiceProfileOperationState.Activated
                && o.EvidenceVersion == CharacterVoiceConsentEvidence.CurrentEvidenceVersion
                && o.AttestationVersion == CharacterVoiceConsentEvidence.CurrentAttestationVersion && o.FormalNarrationAllowed));

    public static async Task RequireAsync(StoryVoiceDbContext db, Guid ownerId,
        IEnumerable<(string Provider, string Voice)> voices, CancellationToken ct)
    {
        var ids = voices.Select(v => Parse(v.Provider, v.Voice)).Distinct().ToArray();
        if (await Eligible(db, ownerId).CountAsync(p => ids.Contains(p.Id), ct) != ids.Length)
            throw new InvalidOperationException(Error);
    }
}

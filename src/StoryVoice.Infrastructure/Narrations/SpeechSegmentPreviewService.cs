using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Persistence;

namespace StoryVoice.Infrastructure.Narrations;

/// <summary>
/// Synthesizes a single draft segment with the voice that segment would actually use in the
/// staged rebuild: the assigned character's fixed voice, or the series narrator. The segment text
/// is sliced server-side from the owner's chapter and hash-verified, so the client never submits
/// arbitrary text to a synthesis endpoint. Only locally hosted providers (BlueMagpie, linked 3wa
/// clone profiles) can preview; cloud providers synthesize exclusively inside batch worker jobs.
/// </summary>
internal sealed class SpeechSegmentPreviewService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser,
    IBlueMagpieTtsClient blueMagpieClient,
    IOptions<BlueMagpieOptions> blueMagpieOptions,
    ILocalClonePreviewService localClonePreviewService) : ISpeechSegmentPreviewService
{
    /// <summary>Matches <c>LocalClonePreviewService.MaximumPreviewTextLength</c>.</summary>
    private const int MaximumClonePreviewChars = 200;

    private static readonly char[] PreferredBreakCharacters =
        ['。', '！', '？', '!', '?', '；', ';', '，', ',', '、'];

    public async Task<SpeechSegmentPreviewAudio?> PreviewSegmentAsync(
        Guid seriesId,
        Guid draftId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var draft = await dbContext.ChapterSpeechPlanDrafts
            .AsNoTracking()
            .AsSplitQuery()
            .Include(candidate => candidate.Segments)
            .SingleOrDefaultAsync(
                candidate => candidate.OwnerId == ownerId
                    && candidate.SeriesId == seriesId
                    && candidate.Id == draftId,
                cancellationToken);
        var segment = draft?.Segments.SingleOrDefault(candidate => candidate.Id == segmentId);
        if (draft is null || segment is null)
        {
            return null;
        }

        var text = await LoadVerifiedSegmentTextAsync(ownerId, draft, segment, cancellationToken);
        if (segment.CharacterId is Guid characterId)
        {
            var character = await dbContext.SeriesCharacters
                .AsNoTracking()
                .Where(candidate => candidate.OwnerId == ownerId
                    && candidate.SeriesId == seriesId
                    && candidate.Id == characterId)
                .Select(candidate => new VoiceTarget(
                    candidate.VoiceProvider,
                    candidate.Voice,
                    candidate.CharacterProfileId))
                .SingleOrDefaultAsync(cancellationToken);
            return character is null
                ? null
                : await SynthesizeAsync(seriesId, characterId, character, text, cancellationToken);
        }

        var narrator = await dbContext.StorySeries
            .AsNoTracking()
            .Where(candidate => candidate.OwnerId == ownerId && candidate.Id == seriesId)
            .Select(candidate => new VoiceTarget(
                candidate.NarratorProvider,
                candidate.NarratorVoice,
                null))
            .SingleOrDefaultAsync(cancellationToken);
        return narrator is null
            ? null
            : await SynthesizeAsync(seriesId, characterId: null, narrator, text, cancellationToken);
    }

    private async Task<SpeechSegmentPreviewAudio?> SynthesizeAsync(
        Guid seriesId,
        Guid? characterId,
        VoiceTarget target,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.Equals(target.Provider, CharacterVoiceProviders.BlueMagpie, StringComparison.OrdinalIgnoreCase))
        {
            if (!blueMagpieOptions.Value.Enabled)
            {
                throw new SeriesVoicePreviewUnavailableException();
            }

            var result = await blueMagpieClient.SynthesizeAsync(
                TruncateForPreview(text, BlueMagpieOptions.MaximumTextScalarsPerChunk),
                target.Voice,
                cancellationToken);
            return new SpeechSegmentPreviewAudio(result.Content, result.ContentType);
        }

        if (string.Equals(target.Provider, CharacterVoiceProviders.ThreeWaVoxCpm2, StringComparison.OrdinalIgnoreCase)
            && characterId is Guid cloneCharacterId
            && target.CharacterProfileId is not null)
        {
            var preview = await localClonePreviewService.PreviewAsync(
                seriesId,
                cloneCharacterId,
                new LocalClonePreviewRequest(TruncateForPreview(text, MaximumClonePreviewChars)),
                cancellationToken);
            return preview is null
                ? null
                : new SpeechSegmentPreviewAudio(preview.Content, preview.ContentType);
        }

        throw new SpeechSegmentPreviewUnsupportedProviderException(target.Provider);
    }

    private async Task<string> LoadVerifiedSegmentTextAsync(
        Guid ownerId,
        ChapterSpeechPlanDraft draft,
        SpeechSegmentDraft segment,
        CancellationToken cancellationToken)
    {
        var isMember = await dbContext.SeriesBooks.AsNoTracking().AnyAsync(
            seriesBook => seriesBook.OwnerId == ownerId
                && seriesBook.SeriesId == draft.SeriesId
                && seriesBook.BookId == draft.BookId,
            cancellationToken);
        var bookOwned = await dbContext.Books.AsNoTracking().AnyAsync(
            candidate => candidate.Id == draft.BookId && candidate.OwnerId == ownerId,
            cancellationToken);
        if (!isMember || !bookOwned)
        {
            throw new InvalidOperationException("劇本草稿的書籍已不在這個系列中，無法試聽。");
        }

        var chapter = await dbContext.Chapters
            .AsNoTracking()
            .Where(candidate => candidate.BookId == draft.BookId && candidate.Id == draft.ChapterId)
            .Select(candidate => new { candidate.Title, candidate.OriginalText })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("劇本草稿的章節已不存在，無法試聽。");

        var source = segment.SourceKind == SpeechSegmentSourceKind.ChapterTitle
            ? chapter.Title
            : chapter.OriginalText;
        if (segment.StartOffset < 0
            || segment.Length <= 0
            || segment.StartOffset + segment.Length > source.Length
            || !string.Equals(
                HashSlice(source.Substring(segment.StartOffset, segment.Length)),
                segment.TextHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("章節內容已變更，請重新產生劇本草稿後再試聽。");
        }

        return source.Substring(segment.StartOffset, segment.Length);
    }

    /// <summary>
    /// A narrator segment can span an entire scene while providers only accept short preview
    /// inputs, so previews clip to the provider limit — at a punctuation boundary when one exists
    /// in the tail half of the budget. Counting Unicode scalars keeps surrogate pairs intact and
    /// satisfies BlueMagpie's scalar-based chunk limit.
    /// </summary>
    private static string TruncateForPreview(string text, int maximumScalars)
    {
        if (ScalarCountAtMost(text, maximumScalars))
        {
            return text;
        }

        var clipped = ClipToScalars(text, maximumScalars);
        var breakIndex = clipped.LastIndexOfAny(PreferredBreakCharacters);
        return breakIndex >= clipped.Length / 2
            ? clipped[..(breakIndex + 1)]
            : clipped;
    }

    private static bool ScalarCountAtMost(string text, int maximumScalars)
    {
        var total = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            if (++total > maximumScalars)
            {
                return false;
            }
        }

        return true;
    }

    private static string ClipToScalars(string text, int maximumScalars)
    {
        var scalars = 0;
        var utf16Length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (scalars == maximumScalars)
            {
                break;
            }

            scalars++;
            utf16Length += rune.Utf16SequenceLength;
        }

        return text[..utf16Length];
    }

    private static string HashSlice(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private Guid EnsureCurrentOwnerId()
    {
        if (currentUser.UserId == Guid.Empty)
        {
            throw new InvalidOperationException("單句試聽需要已驗證的使用者。");
        }

        return currentUser.UserId;
    }

    private sealed record VoiceTarget(
        string Provider,
        string Voice,
        Guid? CharacterProfileId);
}

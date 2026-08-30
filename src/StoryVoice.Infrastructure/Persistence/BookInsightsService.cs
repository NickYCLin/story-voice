using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Books;
using StoryVoice.Application.Insights;
using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Insights;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class BookInsightsService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser,
    ILocalLlmCharacterAnalysisProvider localLlmCharacterAnalysisProvider) : IBookInsightsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BookContentLinkResponse?> GetContentLinkAsync(
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var target = await OwnedBooks()
            .SingleOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        if (target?.ContentBookId is not Guid contentBookId)
        {
            return null;
        }

        var content = await OwnedBooks()
            .Include(book => book.Chapters)
            .SingleOrDefaultAsync(book => book.Id == contentBookId, cancellationToken);
        return content is null ? null : ToContentLink(target, content);
    }

    public async Task<BookContentLinkResponse?> SetContentLinkAsync(
        Guid bookId,
        Guid contentBookId,
        CancellationToken cancellationToken)
    {
        var target = await OwnedBooks(tracking: true)
            .SingleOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var content = await OwnedBooks()
            .Include(book => book.Chapters)
            .SingleOrDefaultAsync(book => book.Id == contentBookId, cancellationToken);
        EnsureProcessable(content);

        if (target.ContentBookId != contentBookId)
        {
            target.LinkAuthorizedContent(contentBookId);
            await RemoveExistingCharacterAnalysisAsync(bookId, cancellationToken);
            await DetachChapterNotesAsync(bookId, cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToContentLink(target, content!);
    }

    public async Task<bool> RemoveContentLinkAsync(Guid bookId, CancellationToken cancellationToken)
    {
        var target = await OwnedBooks(tracking: true)
            .SingleOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        if (target is null)
        {
            return false;
        }

        if (target.ContentBookId is null)
        {
            return true;
        }

        target.UnlinkAuthorizedContent();
        await RemoveExistingCharacterAnalysisAsync(bookId, cancellationToken);
        await DetachChapterNotesAsync(bookId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ExtractiveBookSummaryResponse?> GetSummaryAsync(
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var targetExists = await OwnedBooks().AnyAsync(book => book.Id == bookId, cancellationToken);
        if (!targetExists)
        {
            return null;
        }

        var summary = await dbContext.BookExtractiveSummaries
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.BookId == bookId && item.OwnerId == currentUser.UserId,
                cancellationToken);
        return summary is null ? null : ToResponse(summary);
    }

    public async Task<ExtractiveBookSummaryResponse?> GenerateSummaryAsync(
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var target = await OwnedBooks()
            .Include(book => book.Chapters)
            .SingleOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var content = target.ContentBookId is Guid contentBookId
            ? await OwnedBooks().Include(book => book.Chapters)
                .SingleOrDefaultAsync(book => book.Id == contentBookId, cancellationToken)
            : target;
        EnsureProcessable(content);

        var draft = ExtractiveSummaryGenerator.Generate(content!.Chapters);
        var excerptsJson = JsonSerializer.Serialize(draft.Excerpts, JsonOptions);
        var summary = await dbContext.BookExtractiveSummaries
            .SingleOrDefaultAsync(
                item => item.BookId == bookId && item.OwnerId == currentUser.UserId,
                cancellationToken);

        if (summary is null)
        {
            summary = BookExtractiveSummary.Create(
                currentUser.UserId,
                target.Id,
                content.Id,
                draft.SourceHash,
                excerptsJson);
            dbContext.BookExtractiveSummaries.Add(summary);
        }
        else if (!string.Equals(summary.SourceHash, draft.SourceHash, StringComparison.Ordinal)
            || summary.ContentBookId != content.Id
            || !string.Equals(summary.Version, ExtractiveSummaryGenerator.Version, StringComparison.Ordinal))
        {
            summary.Replace(content.Id, draft.SourceHash, excerptsJson);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(summary);
    }

    public async Task<LocalLlmCharacterAnalysisResponse?> GetCharacterAnalysisAsync(
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var targetExists = await OwnedBooks().AnyAsync(book => book.Id == bookId, cancellationToken);
        if (!targetExists)
        {
            return null;
        }

        var analysis = await dbContext.BookLocalLlmCharacterAnalyses
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.BookId == bookId && item.OwnerId == currentUser.UserId,
                cancellationToken);
        return analysis is null ? null : ToResponse(analysis);
    }

    public async Task<LocalLlmCharacterAnalysisResponse?> GenerateCharacterAnalysisAsync(
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var target = await OwnedBooks()
            .Include(book => book.Chapters)
            .SingleOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var content = target.ContentBookId is Guid contentBookId
            ? await OwnedBooks().Include(book => book.Chapters)
                .SingleOrDefaultAsync(book => book.Id == contentBookId, cancellationToken)
            : target;
        EnsureProcessable(content);

        var source = LocalLlmCharacterAnalysisSource.Create(content!.Chapters);
        var chapterCandidates = await localLlmCharacterAnalysisProvider.AnalyzeAsync(source, cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var currentTarget = await GetLockedOwnedBookForCharacterAnalysisAsync(bookId, cancellationToken);
        if (currentTarget is null)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return null;
        }

        var currentContent = currentTarget.ContentBookId is Guid currentContentBookId
            ? await OwnedBooks().Include(book => book.Chapters)
                .SingleOrDefaultAsync(book => book.Id == currentContentBookId, cancellationToken)
            : await OwnedBooks().Include(book => book.Chapters)
                .SingleOrDefaultAsync(book => book.Id == currentTarget.Id, cancellationToken);
        if (currentContent is null || currentContent.Id != content.Id)
        {
            throw new LocalLlmCharacterAnalysisSourceChangedException();
        }

        EnsureProcessable(currentContent);
        var currentSource = LocalLlmCharacterAnalysisSource.Create(currentContent.Chapters);
        if (!string.Equals(currentSource.SourceHash, source.SourceHash, StringComparison.Ordinal))
        {
            throw new LocalLlmCharacterAnalysisSourceChangedException();
        }

        var canonicalNamesByIdentity = await GetSeriesCanonicalNamesByIdentityAsync(
            currentTarget.Id,
            cancellationToken);
        var mergedCandidates = LocalLlmCharacterAnalysisSource.Merge(
            chapterCandidates.Select(item => (item.ChapterNumber, item.Candidates)),
            canonicalNamesByIdentity);
        var candidatesJson = JsonSerializer.Serialize(mergedCandidates, JsonOptions);

        var analysis = await dbContext.BookLocalLlmCharacterAnalyses
            .SingleOrDefaultAsync(
                item => item.BookId == bookId && item.OwnerId == currentUser.UserId,
                cancellationToken);
        if (analysis is null)
        {
            analysis = BookLocalLlmCharacterAnalysis.Create(
                currentUser.UserId,
                target.Id,
                content.Id,
                localLlmCharacterAnalysisProvider.Model,
                LocalLlmCharacterAnalysisSource.PromptVersion,
                source.SourceHash,
                candidatesJson);
            dbContext.BookLocalLlmCharacterAnalyses.Add(analysis);
        }
        else
        {
            analysis.Replace(
                content.Id,
                localLlmCharacterAnalysisProvider.Model,
                LocalLlmCharacterAnalysisSource.PromptVersion,
                source.SourceHash,
                candidatesJson);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return ToResponse(analysis);
    }

    public async Task<IReadOnlyList<ReadingNoteResponse>?> ListNotesAsync(
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var targetExists = await OwnedBooks().AnyAsync(book => book.Id == bookId, cancellationToken);
        if (!targetExists)
        {
            return null;
        }

        var notes = await dbContext.ReadingNotes
            .AsNoTracking()
            .Where(note => note.OwnerId == currentUser.UserId && note.BookId == bookId)
            .OrderByDescending(note => note.UpdatedAt)
            .ToListAsync(cancellationToken);
        return notes.Select(ToResponse).ToArray();
    }

    public async Task<ReadingNoteResponse?> CreateNoteAsync(
        Guid bookId,
        CreateReadingNoteRequest request,
        CancellationToken cancellationToken)
    {
        var target = await OwnedBooks()
            .SingleOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        await ValidateChapterAsync(target, request.ChapterId, cancellationToken);
        var note = ReadingNote.Create(currentUser.UserId, bookId, request.ChapterId, request.Body);
        dbContext.ReadingNotes.Add(note);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(note);
    }

    public async Task<ReadingNoteResponse?> UpdateNoteAsync(
        Guid bookId,
        Guid noteId,
        UpdateReadingNoteRequest request,
        CancellationToken cancellationToken)
    {
        var note = await dbContext.ReadingNotes.SingleOrDefaultAsync(
            item => item.Id == noteId
                && item.BookId == bookId
                && item.OwnerId == currentUser.UserId,
            cancellationToken);
        if (note is null)
        {
            return null;
        }

        note.Update(request.Body);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(note);
    }

    public async Task<bool> DeleteNoteAsync(
        Guid bookId,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        var note = await dbContext.ReadingNotes.SingleOrDefaultAsync(
            item => item.Id == noteId
                && item.BookId == bookId
                && item.OwnerId == currentUser.UserId,
            cancellationToken);
        if (note is null)
        {
            return false;
        }

        dbContext.ReadingNotes.Remove(note);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<Book?> GetLockedOwnedBookForCharacterAnalysisAsync(
        Guid bookId,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return await OwnedBooks(tracking: true)
                .SingleOrDefaultAsync(book => book.Id == bookId, cancellationToken);
        }

        return await dbContext.Books
            .FromSqlInterpolated($"SELECT * FROM books WHERE \"Id\" = {bookId} AND \"OwnerId\" = {currentUser.UserId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, string>> GetSeriesCanonicalNamesByIdentityAsync(
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var ownerId = currentUser.UserId;
        var identities = await (
            from membership in dbContext.SeriesBooks.AsNoTracking()
            join identity in dbContext.SeriesCharacterIdentityKeys.AsNoTracking()
                on membership.SeriesId equals identity.SeriesId
            join character in dbContext.SeriesCharacters.AsNoTracking()
                on identity.CharacterId equals character.Id
            where membership.OwnerId == ownerId
                && membership.BookId == bookId
                && identity.OwnerId == ownerId
                && character.OwnerId == ownerId
            select new
            {
                identity.NormalizedValue,
                character.CanonicalName,
            })
            .ToArrayAsync(cancellationToken);

        return identities.ToDictionary(
            identity => identity.NormalizedValue,
            identity => identity.CanonicalName,
            StringComparer.Ordinal);
    }

    private IQueryable<Book> OwnedBooks(bool tracking = false)
    {
        var query = dbContext.Books.Where(book => book.OwnerId == currentUser.UserId);
        return tracking ? query : query.AsNoTracking();
    }

    private static void EnsureProcessable(Book? book)
    {
        if (!AuthorizedTextPolicy.IsProcessable(book))
        {
            throw new BookTextUnavailableException(
                "這本書目前沒有可處理的合法正文；請先匯入你合法持有、無 DRM 的 EPUB 或 TXT，再從匯入後的書籍使用正文功能。");
        }
    }

    private async Task ValidateChapterAsync(
        Book target,
        Guid? chapterId,
        CancellationToken cancellationToken)
    {
        if (chapterId is null)
        {
            return;
        }

        var contentBookId = target.ContentBookId ?? target.Id;
        var content = await OwnedBooks()
            .Include(book => book.Chapters)
            .SingleOrDefaultAsync(book => book.Id == contentBookId, cancellationToken);
        EnsureProcessable(content);
        var belongsToAuthorizedContent = content!.Chapters.Any(chapter => chapter.Id == chapterId);
        if (!belongsToAuthorizedContent)
        {
            throw new ArgumentException("章節不屬於這本書已連結的合法正文。", nameof(chapterId));
        }
    }

    private async Task RemoveExistingCharacterAnalysisAsync(Guid bookId, CancellationToken cancellationToken)
    {
        var analysis = await dbContext.BookLocalLlmCharacterAnalyses
            .SingleOrDefaultAsync(item => item.BookId == bookId, cancellationToken);
        if (analysis is not null)
        {
            dbContext.BookLocalLlmCharacterAnalyses.Remove(analysis);
        }
    }

    private async Task DetachChapterNotesAsync(Guid bookId, CancellationToken cancellationToken)
    {
        var notes = await dbContext.ReadingNotes
            .Where(note => note.OwnerId == currentUser.UserId
                && note.BookId == bookId
                && note.ChapterId != null)
            .ToListAsync(cancellationToken);
        foreach (var note in notes)
        {
            note.DetachChapter();
        }
    }

    private static BookContentLinkResponse ToContentLink(Book target, Book content) =>
        new(target.Id, content.Id, content.Title, content.Chapters.Count);

    private static ReadingNoteResponse ToResponse(ReadingNote note) =>
        new(note.Id, note.BookId, note.ChapterId, note.Body, note.CreatedAt, note.UpdatedAt);

    private static LocalLlmCharacterAnalysisResponse ToResponse(BookLocalLlmCharacterAnalysis analysis) =>
        new(
            analysis.BookId,
            analysis.ContentBookId,
            analysis.Generator,
            analysis.Model,
            analysis.PromptVersion,
            analysis.SourceHash,
            analysis.GeneratedAt,
            JsonSerializer.Deserialize<LocalLlmCharacterAnalysisCandidateResponse[]>(analysis.CandidatesJson, JsonOptions) ?? []);

    private static ExtractiveBookSummaryResponse ToResponse(BookExtractiveSummary summary) =>
        new(
            summary.BookId,
            summary.ContentBookId,
            summary.Kind,
            summary.Generator,
            summary.Version,
            summary.SourceHash,
            summary.GeneratedAt,
            JsonSerializer.Deserialize<SummaryExcerptResponse[]>(summary.ExcerptsJson, JsonOptions) ?? []);
}

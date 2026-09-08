using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Books;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Books;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class NarrationService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser,
    IOptions<NarrationOptions> options) : INarrationService
{
    public async Task<IReadOnlyList<NarrationJobResponse>?> ListAsync(
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var bookExists = await OwnedBooks().AnyAsync(book => book.Id == bookId, cancellationToken);
        if (!bookExists)
        {
            return null;
        }

        var jobs = await dbContext.NarrationJobs
            .AsNoTracking()
            .Where(job => job.OwnerId == currentUser.UserId
                && job.BookId == bookId
                && job.Visibility == NarrationArtifactVisibility.Published)
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);
        return jobs.Select(ToResponse).ToArray();
    }

    public Task<NarrationJobResponse?> CreateAsync(
        Guid bookId,
        CreateNarrationRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<NarrationJobResponse?>(new SingleVoiceNarrationRetiredException());

    public async Task<NarrationJobResponse?> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await OwnedRegularJobs().SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        return job is null ? null : ToResponse(job);
    }

    public async Task<NarrationJobResponse?> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            dbContext.ChangeTracker.Clear();
            var job = await dbContext.NarrationJobs.SingleOrDefaultAsync(
                item => item.Id == jobId
                    && item.OwnerId == currentUser.UserId
                    && item.Visibility == NarrationArtifactVisibility.Published
                    && dbContext.Books.Any(book =>
                        book.Id == item.BookId
                        && book.OwnerId == currentUser.UserId
                        && !book.IsArchived),
                cancellationToken);
            if (job is null)
            {
                return null;
            }

            if (job.Status is NarrationJobStatus.Completed or NarrationJobStatus.Failed or NarrationJobStatus.Cancelled)
            {
                return ToResponse(job);
            }

            job.RequestCancellation();
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return ToResponse(job);
            }
            catch (DbUpdateConcurrencyException)
            {
                if (attempt == 4)
                {
                    break;
                }
            }
        }

        dbContext.ChangeTracker.Clear();
        var latest = await OwnedRegularJobs().SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        return latest is null ? null : ToResponse(latest);
    }

    public async Task<NarrationAudioDescriptor?> GetAudioAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await OwnedRegularJobs().SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || !job.IsAvailableForRegularPlayback)
        {
            return null;
        }

        var root = Path.GetFullPath(options.Value.AudioRootPath);
        var path = Path.GetFullPath(Path.Combine(root, job.AudioRelativePath!));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }

        return new NarrationAudioDescriptor(path, "audio/mpeg");
    }

    public async Task<NarrationTimelineResponse?> GetTimelineAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await OwnedRegularJobs().SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || !job.IsAvailableForRegularPlayback)
        {
            return null;
        }

        var timeline = await dbContext.NarrationTimelines
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.NarrationJobId == jobId && item.OwnerId == currentUser.UserId,
                cancellationToken);
        if (timeline is null)
        {
            return null;
        }

        var document = NarrationTimelineDocument.FromJson(timeline.TimelineJson);
        if (document is null)
        {
            return null;
        }

        var contentBook = await dbContext.Books
            .AsNoTracking()
            .Include(book => book.Chapters)
            .SingleOrDefaultAsync(
                book => book.Id == job.ContentBookId && book.OwnerId == currentUser.UserId,
                cancellationToken);
        var chaptersById = contentBook?.Chapters.ToDictionary(chapter => chapter.Id)
            ?? new Dictionary<Guid, Chapter>();
        var textAvailable = contentBook is not null
            && SourceHashStillMatches(contentBook, job.SourceHash);
        var characterNames = await LoadCharacterNamesAsync(job.SeriesId, document, cancellationToken);

        var chapters = new List<NarrationTimelineChapterResponse>();
        var turns = new List<NarrationTimelineTurnResponse>(document.Turns.Count);
        for (var index = 0; index < document.Turns.Count; index++)
        {
            var turn = document.Turns[index];
            if (turn.ChapterStart || chapters.Count == 0)
            {
                chapters.Add(new NarrationTimelineChapterResponse(
                    turn.ChapterId,
                    turn.ChapterSortOrder,
                    chaptersById.TryGetValue(turn.ChapterId, out var chapter) ? chapter.Title : string.Empty,
                    turn.StartMs));
            }

            var (kind, characterId) = ResolveTurnIdentity(turn);
            turns.Add(new NarrationTimelineTurnResponse(
                index,
                turn.StartMs,
                turn.DurationMs,
                turn.ChapterSortOrder,
                kind,
                characterId,
                characterId is Guid id ? characterNames.GetValueOrDefault(id) : null,
                textAvailable ? SliceTurnText(turn, chaptersById) : null));
        }

        return new NarrationTimelineResponse(job.Id, textAvailable, chapters, turns);
    }

    private static bool SourceHashStillMatches(Book contentBook, string expectedSourceHash)
    {
        try
        {
            var source = NarrationSource.Create(contentBook.Chapters.Select(chapter =>
                new NarrationChapterSource(chapter.Id, chapter.SortOrder, chapter.Title, chapter.OriginalText)));
            return string.Equals(source.SourceHash, expectedSourceHash, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<ListeningProgressResponse?> GetListeningProgressAsync(
        Guid jobId, CancellationToken cancellationToken)
    {
        if (!await CanSaveListeningProgressAsync(jobId, cancellationToken)) return null;
        var progress = await dbContext.ListeningProgress.AsNoTracking().SingleOrDefaultAsync(
            item => item.NarrationJobId == jobId && item.OwnerId == currentUser.UserId, cancellationToken);
        return ToProgressResponse(jobId, progress);
    }

    public async Task<SaveListeningProgressResult?> SaveListeningProgressAsync(
        Guid jobId, SaveListeningProgressRequest request, CancellationToken cancellationToken)
    {
        if (!await CanSaveListeningProgressAsync(jobId, cancellationToken)) return null;
        var progress = await dbContext.ListeningProgress.SingleOrDefaultAsync(
            item => item.NarrationJobId == jobId && item.OwnerId == currentUser.UserId, cancellationToken);
        if (progress?.Version != request.ExpectedVersion)
            return new(ToProgressResponse(jobId, progress), Conflict: true);

        var creating = progress is null;
        if (progress is null)
        {
            progress = ListeningProgress.Create(currentUser.UserId, jobId, request.PositionMs, request.DurationMs);
            dbContext.ListeningProgress.Add(progress);
        }
        else
        {
            progress.Update(request.PositionMs, request.DurationMs);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(ToProgressResponse(jobId, progress), Conflict: false);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.Entry(progress).State = EntityState.Detached;
            return await ReadProgressConflictAsync(jobId, cancellationToken);
        }
        catch (DbUpdateException exception) when (creating
            && exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(progress).State = EntityState.Detached;
            return await ReadProgressConflictAsync(jobId, cancellationToken);
        }
    }

    private Task<bool> CanSaveListeningProgressAsync(Guid jobId, CancellationToken cancellationToken) =>
        OwnedRegularJobs().AnyAsync(job => job.Id == jobId && job.Status == NarrationJobStatus.Completed
            && job.AudioRelativePath != null && job.AudioBytes > 0
            && dbContext.Books.Any(book => book.Id == job.BookId && book.OwnerId == currentUser.UserId && !book.IsArchived),
            cancellationToken);

    private async Task<SaveListeningProgressResult> ReadProgressConflictAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var latest = await dbContext.ListeningProgress.AsNoTracking().SingleOrDefaultAsync(
            item => item.NarrationJobId == jobId && item.OwnerId == currentUser.UserId, cancellationToken);
        return new(ToProgressResponse(jobId, latest), Conflict: true);
    }

    private static ListeningProgressResponse ToProgressResponse(Guid jobId, ListeningProgress? progress) =>
        new(jobId, progress?.PositionMs ?? 0, progress?.DurationMs ?? 0, progress?.Version, progress?.UpdatedAt);

    private async Task<IReadOnlyDictionary<Guid, string>> LoadCharacterNamesAsync(
        Guid? seriesId,
        NarrationTimelineDocument document,
        CancellationToken cancellationToken)
    {
        if (seriesId is not Guid series)
        {
            return new Dictionary<Guid, string>();
        }

        var characterIds = document.Turns
            .SelectMany(turn => turn.Slices)
            .Where(slice => slice.CharacterId is not null)
            .Select(slice => slice.CharacterId!.Value)
            .Distinct()
            .ToArray();
        if (characterIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await dbContext.SeriesCharacters
            .AsNoTracking()
            .Where(character => character.OwnerId == currentUser.UserId
                && character.SeriesId == series
                && characterIds.Contains(character.Id))
            .ToDictionaryAsync(
                character => character.Id,
                character => character.CanonicalName,
                cancellationToken);
    }

    /// <summary>
    /// A merged turn can fold several confirmed segments into one stretch of audio. It only keeps
    /// a single story identity when every slice agrees; anything blended (e.g. narration merged
    /// with a dialogue line that fell back to the narrator voice) reports "mixed" rather than
    /// mislabelling narration as one character's line.
    /// </summary>
    private static (string Kind, Guid? CharacterId) ResolveTurnIdentity(NarrationTimelineTurn turn)
    {
        var kinds = turn.Slices.Select(slice => slice.Kind).Distinct().ToArray();
        var characterIds = turn.Slices.Select(slice => slice.CharacterId).Distinct().ToArray();
        if (kinds.Length == 1
            && string.Equals(kinds[0], NarrationTimelineTurnKinds.Narrator, StringComparison.Ordinal))
        {
            return (NarrationTimelineTurnKinds.Narrator, null);
        }

        if (kinds.Length == 1 && characterIds.Length == 1 && characterIds[0] is Guid characterId)
        {
            return (kinds[0], characterId);
        }

        return (NarrationTimelineTurnKinds.Mixed, null);
    }

    private static string? SliceTurnText(
        NarrationTimelineTurn turn,
        IReadOnlyDictionary<Guid, Chapter> chaptersById)
    {
        if (!chaptersById.TryGetValue(turn.ChapterId, out var chapter))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var slice in turn.Slices)
        {
            var sourceText = string.Equals(
                slice.SourceKind,
                NarrationTimelineSourceKinds.ChapterTitle,
                StringComparison.Ordinal)
                ? chapter.Title
                : chapter.OriginalText;
            if (slice.StartOffset < 0
                || slice.Length < 1
                || slice.StartOffset > sourceText.Length
                || slice.Length > sourceText.Length - slice.StartOffset)
            {
                return null;
            }

            builder.Append(sourceText, slice.StartOffset, slice.Length);
        }

        return builder.ToString();
    }

    private async Task<NarrationJobResponse?> RequeueIfTerminalAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            dbContext.ChangeTracker.Clear();
            var job = await dbContext.NarrationJobs.SingleOrDefaultAsync(
                item => item.Id == jobId
                    && item.OwnerId == currentUser.UserId
                    && item.Visibility == NarrationArtifactVisibility.Published
                    && dbContext.Books.Any(book =>
                        book.Id == item.BookId
                        && book.OwnerId == currentUser.UserId
                        && !book.IsArchived),
                cancellationToken);
            if (job is null)
            {
                return null;
            }

            if (job.Status is not (NarrationJobStatus.Failed or NarrationJobStatus.Cancelled))
            {
                return ToResponse(job);
            }

            job.Requeue(DateTimeOffset.UtcNow);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return ToResponse(job);
            }
            catch (DbUpdateConcurrencyException)
            {
                if (attempt == 4)
                {
                    break;
                }
            }
        }

        dbContext.ChangeTracker.Clear();
        var latest = await OwnedRegularJobs().SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        return latest is null ? null : ToResponse(latest);
    }

    private IQueryable<Book> OwnedBooks() =>
        dbContext.Books.AsNoTracking().Where(book => book.OwnerId == currentUser.UserId);

    private IQueryable<NarrationJob> OwnedRegularJobs() =>
        dbContext.NarrationJobs.AsNoTracking().Where(job =>
            job.OwnerId == currentUser.UserId
            && job.Visibility == NarrationArtifactVisibility.Published);

    private static NarrationJobResponse ToResponse(NarrationJob job) =>
        new(
            job.Id,
            job.BookId,
            job.ContentBookId,
            job.SourceHash,
            job.Voice,
            job.Rate,
            job.Status.ToString(),
            job.ProgressPercent,
            job.Attempts,
            job.CancellationRequested,
            job.ErrorCode,
            job.AudioBytes,
            job.RightsAttestedAt,
            job.CreatedAt,
            job.UpdatedAt,
            job.CompletedAt);
}

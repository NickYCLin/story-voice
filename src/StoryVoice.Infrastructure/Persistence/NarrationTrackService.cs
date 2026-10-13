using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class NarrationTrackService(StoryVoiceDbContext db, ICurrentUser user,
    IOptions<NarrationOptions> options) : INarrationTrackService
{
    private Task<bool> OwnsBookAsync(Guid bookId, CancellationToken ct) => db.Books.AnyAsync(book =>
        book.Id == bookId && book.OwnerId == user.UserId && !book.IsArchived, ct);
    private IQueryable<NarrationJob> Jobs(Guid bookId) => db.NarrationJobs.AsNoTracking()
        .Where(job => job.OwnerId == user.UserId && job.BookId == bookId);
    private Task<NarrationTrackSelection?> LatestAsync(Guid bookId, CancellationToken ct) =>
        db.NarrationTrackSelections.AsNoTracking().Where(item => item.OwnerId == user.UserId && item.BookId == bookId)
            .OrderByDescending(item => item.Revision).FirstOrDefaultAsync(ct);

    public async Task<NarrationTrackState?> ListAsync(Guid bookId, CancellationToken cancellationToken)
    {
        if (!await OwnsBookAsync(bookId, cancellationToken)) return null;
        var jobs = await Jobs(bookId).OrderByDescending(job => job.CreatedAt).ToListAsync(cancellationToken);
        var selected = await LatestAsync(bookId, cancellationToken);
        return new(bookId, selected?.Revision ?? 0, selected?.NarrationJobId, jobs.Select(job =>
        {
            var available = ResolveAudio(job) is not null;
            return new NarrationTrackVersion(job.Id, job.Mode.ToString(), job.Visibility.ToString(), job.Status.ToString(),
                job.CastRevisionId, job.RebuildBatchId, job.SourceHash, job.ConcurrencyStamp, job.AudioBytes, job.CreatedAt,
                available, available ? null : job.Status != NarrationJobStatus.Completed ? "narration_not_completed" : "narration_audio_unavailable");
        }).ToArray());
    }

    public async Task<SelectNarrationTrackResult?> SelectAsync(Guid bookId, SelectNarrationTrackRequest request, CancellationToken cancellationToken)
    {
        if (request.ExpectedRevision < 0 || request.ExpectedRevision == long.MaxValue)
            throw new ArgumentException("音軌選擇版本不正確。");
        if (!await OwnsBookAsync(bookId, cancellationToken)) return null;
        var job = await Jobs(bookId).SingleOrDefaultAsync(item => item.Id == request.JobId, cancellationToken);
        if (job is null) return null;
        var current = await LatestAsync(bookId, cancellationToken);
        var revision = current?.Revision ?? 0;
        if (job.ConcurrencyStamp != request.ExpectedArtifactVersion || ResolveAudio(job) is null)
            return new((await ListAsync(bookId, cancellationToken))!, true);
        // An exact retry is idempotent. Choosing an older track later appends a new revision.
        if (revision == request.ExpectedRevision + 1 && current?.NarrationJobId == job.Id)
            return new((await ListAsync(bookId, cancellationToken))!, false);
        if (revision != request.ExpectedRevision)
            return new((await ListAsync(bookId, cancellationToken))!, true);
        var selection = NarrationTrackSelection.Create(user.UserId, bookId, job.Id, current?.NarrationJobId, revision + 1);
        db.NarrationTrackSelections.Add(selection);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(selection).State = EntityState.Detached;
            return new((await ListAsync(bookId, cancellationToken))!, true);
        }
        return new((await ListAsync(bookId, cancellationToken))!, false);
    }

    public async Task<NarrationAudioDescriptor?> GetPrivateAudioAsync(Guid bookId, Guid jobId, CancellationToken cancellationToken)
    {
        if (!await OwnsBookAsync(bookId, cancellationToken)) return null;
        var job = await Jobs(bookId).SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        return job is null ? null : ResolveAudio(job);
    }

    private NarrationAudioDescriptor? ResolveAudio(NarrationJob job)
    {
        if (job.Status != NarrationJobStatus.Completed || job.AudioBytes is not > 0 || string.IsNullOrWhiteSpace(job.AudioRelativePath)) return null;
        try
        {
            var path = CharacterVoiceReferenceFiles.Resolve(options.Value.AudioRootPath, job.AudioRelativePath);
            return new FileInfo(path).Length == job.AudioBytes ? new(path, "audio/mpeg") : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
}

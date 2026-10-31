using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Domain.Narrations;
using StoryVoice.Domain.Series;

namespace StoryVoice.Infrastructure.Persistence;

internal sealed class SpeechPlanService(
    StoryVoiceDbContext dbContext,
    ICurrentUser currentUser,
    IChapterSpeechPlanRepository repository,
    ISpeakerAttributionProvider attributionProvider,
    ChineseSpeechSegmenter segmenter) : ISpeechPlanService
{
    public async Task<ChapterSpeechPlanDraftResponse?> BuildOrRefreshDraftAsync(
        Guid seriesId,
        Guid bookId,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var chapter = await LoadChapterAsync(ownerId, seriesId, bookId, chapterId, cancellationToken);
        if (chapter is null)
        {
            return null;
        }

        var plan = segmenter.Segment(chapter.Title, chapter.OriginalText);
        if (plan.TitleTurn is null)
        {
            throw new InvalidOperationException("章節缺少可朗讀的章名，無法建立劇本草稿。");
        }

        var knownCharacters = await LoadKnownCharactersAsync(ownerId, seriesId, cancellationToken);
        var narrativeVoice = await LoadNarrativeVoiceConfigurationAsync(
            ownerId,
            seriesId,
            cancellationToken);
        var pointOfViewCharacterId = narrativeVoice.PointOfViewCharacterId;
        var knownPointOfViewCharacterId = pointOfViewCharacterId is Guid povCharacterId
            && knownCharacters.Any(character => character.CharacterId == povCharacterId)
                ? povCharacterId
                : (Guid?)null;
        var usesPointOfViewInnerVoice =
            narrativeVoice.Mode == NarrativeVoiceMode.PointOfViewInnerMonologue;
        if (usesPointOfViewInnerVoice && knownPointOfViewCharacterId is null)
        {
            throw new InvalidOperationException(
                "Point-of-view inner-monologue narration requires a valid series character.");
        }

        var hasDialogue = plan.BodySegments.Any(segment => segment.Kind == SpeechSegmentKind.Dialogue);
        var attributionResults = hasDialogue
            ? await attributionProvider.AttributeAsync(
                new SpeakerAttributionRequest(
                    knownCharacters,
                    BuildFullSegmentContext(plan.BodySegments, chapter.OriginalText),
                    pointOfViewCharacterId),
                cancellationToken)
            : [];
        var attributionByIndex = attributionResults.ToDictionary(result => result.SegmentIndex);

        var titleText = chapter.Title[plan.TitleTurn.StartOffset..(plan.TitleTurn.StartOffset + plan.TitleTurn.Length)];
        var segments = new List<DraftSegmentInput>
        {
            new(
                0,
                SpeechSegmentSourceKind.ChapterTitle,
                plan.TitleTurn.StartOffset,
                plan.TitleTurn.Length,
                HashSlice(titleText),
                usesPointOfViewInnerVoice
                    ? SpeechSegmentTurnKind.InnerMonologue
                    : SpeechSegmentTurnKind.Narrator,
                usesPointOfViewInnerVoice ? knownPointOfViewCharacterId : null,
                100,
                SpeechSegmentDecisionSource.Rule,
                SpeechSegmentReviewStatus.Confirmed),
        };

        foreach (var bodySegment in plan.BodySegments)
        {
            var sortOrder = segments.Count;
            var text = chapter.OriginalText.Substring(bodySegment.StartOffset, bodySegment.Length);
            if (bodySegment.Kind != SpeechSegmentKind.Dialogue)
            {
                var usePointOfViewCharacter = usesPointOfViewInnerVoice
                    || (bodySegment.Kind == SpeechSegmentKind.InnerMonologue
                        && knownPointOfViewCharacterId is not null);
                segments.Add(new DraftSegmentInput(
                    sortOrder,
                    SpeechSegmentSourceKind.Body,
                    bodySegment.StartOffset,
                    bodySegment.Length,
                    HashSlice(text),
                    usePointOfViewCharacter
                        ? SpeechSegmentTurnKind.InnerMonologue
                        : SpeechSegmentTurnKind.Narrator,
                    usePointOfViewCharacter ? knownPointOfViewCharacterId : null,
                    100,
                    SpeechSegmentDecisionSource.Rule,
                    SpeechSegmentReviewStatus.Confirmed));
                continue;
            }

            var attribution = attributionByIndex.GetValueOrDefault(bodySegment.Index);
            var (characterId, confidence, decisionSource, reviewStatus) = MapAttribution(attribution);
            segments.Add(new DraftSegmentInput(
                sortOrder,
                SpeechSegmentSourceKind.Body,
                bodySegment.StartOffset,
                bodySegment.Length,
                HashSlice(text),
                SpeechSegmentTurnKind.Dialogue,
                characterId,
                confidence,
                decisionSource,
                reviewStatus));
        }

        var existingDraft = await repository.GetForMutationByChapterAsync(seriesId, bookId, chapterId, cancellationToken);
        if (existingDraft is null)
        {
            var draft = ChapterSpeechPlanDraft.Create(ownerId, seriesId, bookId, chapterId, plan.SourceHash, segments);
            await repository.AddAsync(draft, cancellationToken);
            await SaveChangesAsync(cancellationToken);
            return await ToResponseAsync(draft, knownCharacters, cancellationToken);
        }

        // POST is an explicit rebuild operation. Always replace the generated segments even when
        // chapter text is unchanged, because speaker attribution also depends on the current cast,
        // aliases and point-of-view character. GET remains the non-mutating cached read path.
        existingDraft.RegenerateFromSegmentation(plan.SourceHash, segments);
        await SaveChangesAsync(cancellationToken);
        return await ToResponseAsync(existingDraft, knownCharacters, cancellationToken);
    }

    public async Task<ChapterSpeechPlanDraftResponse?> GetDraftAsync(
        Guid seriesId,
        Guid bookId,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var draft = await repository.GetForMutationByChapterAsync(seriesId, bookId, chapterId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        var knownCharacters = await LoadKnownCharactersAsync(ownerId, seriesId, cancellationToken);
        return await ToResponseAsync(draft, knownCharacters, cancellationToken);
    }

    public async Task<ChapterSpeechPlanDraftResponse?> ConfirmSegmentAsync(
        Guid seriesId,
        Guid draftId,
        Guid segmentId,
        ConfirmSpeechSegmentRequest request,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var draft = await repository.GetForMutationByIdAsync(seriesId, draftId, cancellationToken);
        if (draft is null || draft.Segments.All(segment => segment.Id != segmentId))
        {
            return null;
        }

        draft.ConfirmSegment(segmentId, request.CharacterId);
        await SaveChangesAsync(cancellationToken);
        var knownCharacters = await LoadKnownCharactersAsync(ownerId, seriesId, cancellationToken);
        return await ToResponseAsync(draft, knownCharacters, cancellationToken);
    }

    public async Task<ChapterSpeechPlanDraftResponse?> RejectSegmentAsync(
        Guid seriesId,
        Guid draftId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var draft = await repository.GetForMutationByIdAsync(seriesId, draftId, cancellationToken);
        if (draft is null || draft.Segments.All(segment => segment.Id != segmentId))
        {
            return null;
        }

        draft.RejectSegment(segmentId);
        await SaveChangesAsync(cancellationToken);
        var knownCharacters = await LoadKnownCharactersAsync(ownerId, seriesId, cancellationToken);
        return await ToResponseAsync(draft, knownCharacters, cancellationToken);
    }

    public async Task<ChapterSpeechPlanDraftResponse?> ConfirmSuggestedSegmentsAsync(
        Guid seriesId,
        Guid draftId,
        ConfirmSuggestedSpeechSegmentsRequest request,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var draft = await repository.GetForMutationByIdAsync(seriesId, draftId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        var confirmedCount = draft.ConfirmSuggestedSegments(request.CharacterId);
        if (confirmedCount > 0)
        {
            await SaveChangesAsync(cancellationToken);
        }

        var knownCharacters = await LoadKnownCharactersAsync(ownerId, seriesId, cancellationToken);
        return await ToResponseAsync(draft, knownCharacters, cancellationToken);
    }

    public async Task<ChapterSpeechPlanDraftResponse?> ReassignSegmentAsync(
        Guid seriesId,
        Guid draftId,
        Guid segmentId,
        ReassignSpeechSegmentRequest request,
        CancellationToken cancellationToken)
    {
        var ownerId = EnsureCurrentOwnerId();
        var kind = request.Kind switch
        {
            nameof(SpeechSegmentTurnKind.Narrator) => SpeechSegmentTurnKind.Narrator,
            nameof(SpeechSegmentTurnKind.Dialogue) => SpeechSegmentTurnKind.Dialogue,
            nameof(SpeechSegmentTurnKind.InnerMonologue) => SpeechSegmentTurnKind.InnerMonologue,
            _ => throw new ArgumentException(
                "片段朗讀方式必須是 Narrator、Dialogue 或 InnerMonologue。",
                nameof(request)),
        };

        var draft = await repository.GetForMutationByIdAsync(seriesId, draftId, cancellationToken);
        if (draft is null || draft.Segments.All(segment => segment.Id != segmentId))
        {
            return null;
        }

        draft.ReassignSegment(segmentId, kind, request.CharacterId);
        await SaveChangesAsync(cancellationToken);
        var knownCharacters = await LoadKnownCharactersAsync(ownerId, seriesId, cancellationToken);
        return await ToResponseAsync(draft, knownCharacters, cancellationToken);
    }

    public async Task<ConfirmedSpeechPlanRevisionResponse?> ConfirmPlanAsync(
        Guid seriesId,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var draft = await repository.GetForMutationByIdAsync(seriesId, draftId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        var revisionNumber = await repository.GetNextRevisionNumberAsync(
            seriesId,
            draft.BookId,
            draft.ChapterId,
            cancellationToken);
        var revision = draft.Confirm(revisionNumber, DateTimeOffset.UtcNow);
        await repository.AddConfirmedRevisionAsync(revision, cancellationToken);
        await SaveChangesAsync(cancellationToken);

        return new ConfirmedSpeechPlanRevisionResponse(
            revision.Id,
            revision.SeriesId,
            revision.BookId,
            revision.ChapterId,
            revision.RevisionNumber,
            revision.Fingerprint,
            revision.Segments.Count,
            revision.CreatedAt);
    }

    private static (Guid? CharacterId, int Confidence, SpeechSegmentDecisionSource DecisionSource, SpeechSegmentReviewStatus ReviewStatus)
        MapAttribution(SpeakerAttributionResult? attribution)
    {
        if (attribution is null)
        {
            return (null, 0, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Suggested);
        }

        var decisionSource = attribution.Source == SpeakerAttributionDecisionSource.LocalModel
            ? SpeechSegmentDecisionSource.LocalModel
            : SpeechSegmentDecisionSource.Rule;
        var reviewStatus = attribution.Outcome == SpeakerAttributionOutcome.Confirmed
            ? SpeechSegmentReviewStatus.Confirmed
            : SpeechSegmentReviewStatus.Suggested;
        return (attribution.CharacterId, attribution.Confidence, decisionSource, reviewStatus);
    }

    private static IReadOnlyList<SpeechSegmentAttributionInput> BuildFullSegmentContext(
        IReadOnlyList<SpeechSegment> bodySegments,
        string body) =>
        bodySegments
            .Select(segment => new SpeechSegmentAttributionInput(
                segment.Index,
                segment.Kind,
                body.Substring(segment.StartOffset, segment.Length)))
            .ToArray();

    private async Task<ChapterContext?> LoadChapterAsync(
        Guid ownerId,
        Guid seriesId,
        Guid bookId,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var isMember = await dbContext.SeriesBooks.AsNoTracking().AnyAsync(
            seriesBook => seriesBook.OwnerId == ownerId
                && seriesBook.SeriesId == seriesId
                && seriesBook.BookId == bookId,
            cancellationToken);
        if (!isMember)
        {
            return null;
        }

        var chapter = await dbContext.Chapters
            .AsNoTracking()
            .Where(candidate => candidate.BookId == bookId && candidate.Id == chapterId)
            .Select(candidate => new ChapterContext(candidate.Title, candidate.OriginalText))
            .SingleOrDefaultAsync(cancellationToken);
        if (chapter is null)
        {
            return null;
        }

        var bookOwned = await dbContext.Books.AsNoTracking().AnyAsync(
            candidate => candidate.Id == bookId && candidate.OwnerId == ownerId,
            cancellationToken);
        return bookOwned ? chapter : null;
    }

    private async Task<NarrativeVoiceConfiguration> LoadNarrativeVoiceConfigurationAsync(
        Guid ownerId,
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        var configuration = await dbContext.StorySeries
            .AsNoTracking()
            .Where(series => series.OwnerId == ownerId && series.Id == seriesId)
            .Select(series => new
            {
                series.NarrativeVoiceMode,
                series.PointOfViewCharacterId,
            })
            .SingleOrDefaultAsync(cancellationToken);
        return configuration is null
            ? throw new InvalidOperationException("The speech plan series no longer exists.")
            : new NarrativeVoiceConfiguration(
                configuration.NarrativeVoiceMode,
                configuration.PointOfViewCharacterId);
    }

    private async Task<IReadOnlyList<KnownCharacterIdentity>> LoadKnownCharactersAsync(
        Guid ownerId,
        Guid seriesId,
        CancellationToken cancellationToken)
    {
        var characters = await dbContext.SeriesCharacters
            .AsNoTracking()
            .Where(character => character.OwnerId == ownerId && character.SeriesId == seriesId)
            .Select(character => new { character.Id, character.NormalizedName })
            .ToListAsync(cancellationToken);
        var aliases = await dbContext.SeriesCharacterIdentityKeys
            .AsNoTracking()
            .Where(key => key.OwnerId == ownerId
                && key.SeriesId == seriesId
                && key.Kind == SeriesCharacterIdentityKeyKind.Alias)
            .Select(key => new { key.CharacterId, key.NormalizedValue })
            .ToListAsync(cancellationToken);
        var aliasesByCharacter = aliases
            .GroupBy(alias => alias.CharacterId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(alias => alias.NormalizedValue).ToArray());

        return characters
            .Select(character => new KnownCharacterIdentity(
                character.Id,
                character.NormalizedName,
                aliasesByCharacter.GetValueOrDefault(character.Id, Array.Empty<string>())))
            .ToArray();
    }

    private async Task<ChapterSpeechPlanDraftResponse> ToResponseAsync(
        ChapterSpeechPlanDraft draft,
        IReadOnlyList<KnownCharacterIdentity> knownCharacters,
        CancellationToken cancellationToken)
    {
        var matchingRevisions = await dbContext.ConfirmedSpeechPlanRevisions
            .AsNoTracking()
            .Include(revision => revision.Segments)
            .Where(revision => revision.OwnerId == draft.OwnerId
                && revision.SeriesId == draft.SeriesId
                && revision.BookId == draft.BookId
                && revision.ChapterId == draft.ChapterId
                && revision.SourceHash == draft.SourceHash)
            .OrderByDescending(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken);
        var confirmedRevisionId = matchingRevisions
            .FirstOrDefault(revision => revision.MatchesDraft(draft))
            ?.Id;

        return ToResponse(draft, knownCharacters, confirmedRevisionId);
    }

    private static ChapterSpeechPlanDraftResponse ToResponse(
        ChapterSpeechPlanDraft draft,
        IReadOnlyList<KnownCharacterIdentity> knownCharacters,
        Guid? confirmedRevisionId)
    {
        var namesById = knownCharacters.ToDictionary(character => character.CharacterId, character => character.NormalizedCanonicalName);
        return new ChapterSpeechPlanDraftResponse(
            draft.Id,
            draft.SeriesId,
            draft.BookId,
            draft.ChapterId,
            draft.PlanVersion,
            draft.Status.ToString(),
            confirmedRevisionId,
            draft.Segments
                .OrderBy(segment => segment.SortOrder)
                .Select(segment => new SpeechPlanSegmentResponse(
                    segment.Id,
                    segment.SortOrder,
                    segment.SourceKind.ToString(),
                    segment.Kind.ToString(),
                    segment.StartOffset,
                    segment.Length,
                    segment.CharacterId,
                    segment.CharacterId is Guid characterId ? namesById.GetValueOrDefault(characterId) : null,
                    segment.Confidence,
                    segment.DecisionSource.ToString(),
                    segment.ReviewStatus.ToString()))
                .ToArray(),
            draft.CreatedAt,
            draft.UpdatedAt);
    }

    private static string HashSlice(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException("劇本資料與既有資料衝突，請重新整理後再試。", exception);
        }
    }

    private Guid EnsureCurrentOwnerId()
    {
        if (currentUser.UserId == Guid.Empty)
        {
            throw new InvalidOperationException("劇本管理需要已驗證的使用者。");
        }

        return currentUser.UserId;
    }

    private sealed record ChapterContext(string Title, string OriginalText);

    private sealed record NarrativeVoiceConfiguration(
        NarrativeVoiceMode Mode,
        Guid? PointOfViewCharacterId);
}

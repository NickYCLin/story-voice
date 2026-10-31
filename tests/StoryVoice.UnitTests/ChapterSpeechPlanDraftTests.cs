using StoryVoice.Domain.Narrations;

namespace StoryVoice.UnitTests;

public sealed class ChapterSpeechPlanDraftTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid BookId = Guid.NewGuid();
    private static readonly Guid ChapterId = Guid.NewGuid();
    private static readonly Guid AliceId = Guid.NewGuid();

    [Fact]
    public void Create_requires_the_first_segment_to_be_a_chapter_title_narration_turn()
    {
        var segments = new[]
        {
            new DraftSegmentInput(0, SpeechSegmentSourceKind.Body, 0, 5, Hash("body"), SpeechSegmentTurnKind.Narrator, null, 100, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed),
        };

        Assert.Throws<ArgumentException>(() => CreateDraft(segments));
    }

    [Fact]
    public void Create_allows_the_chapter_title_to_use_the_point_of_view_inner_voice()
    {
        var draft = CreateDraft([PointOfViewTitleTurn()]);

        var title = Assert.Single(draft.Segments);
        Assert.Equal(SpeechSegmentSourceKind.ChapterTitle, title.SourceKind);
        Assert.Equal(SpeechSegmentTurnKind.InnerMonologue, title.Kind);
        Assert.Equal(AliceId, title.CharacterId);
        Assert.Equal(ChapterSpeechPlanDraftStatus.ReadyToConfirm, draft.Status);
    }

    [Fact]
    public void Create_rejects_gaps_or_duplicate_sort_orders()
    {
        var segments = new[]
        {
            TitleTurn(),
            new DraftSegmentInput(2, SpeechSegmentSourceKind.Body, 5, 5, Hash("a"), SpeechSegmentTurnKind.Narrator, null, 100, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed),
        };

        Assert.Throws<ArgumentException>(() => CreateDraft(segments));
    }

    [Fact]
    public void Create_rejects_a_second_chapter_title_segment()
    {
        var segments = new[]
        {
            TitleTurn(),
            new DraftSegmentInput(1, SpeechSegmentSourceKind.ChapterTitle, 0, 5, Hash("title2"), SpeechSegmentTurnKind.Narrator, null, 100, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed),
        };

        Assert.Throws<ArgumentException>(() => CreateDraft(segments));
    }

    [Fact]
    public void Dialogue_segments_must_come_from_the_chapter_body_not_the_title()
    {
        var segments = new[]
        {
            TitleTurn(),
            new DraftSegmentInput(1, SpeechSegmentSourceKind.ChapterTitle, 0, 5, Hash("x"), SpeechSegmentTurnKind.Dialogue, AliceId, 90, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Suggested),
        };

        Assert.Throws<ArgumentException>(() => CreateDraft(segments));
    }

    [Fact]
    public void Narrator_segments_cannot_carry_a_character_id()
    {
        var segments = new[]
        {
            TitleTurn(),
            new DraftSegmentInput(1, SpeechSegmentSourceKind.Body, 0, 5, Hash("n"), SpeechSegmentTurnKind.Narrator, AliceId, 100, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed),
        };

        Assert.Throws<ArgumentException>(() => CreateDraft(segments));
    }

    [Fact]
    public void Inner_monologue_segments_require_a_point_of_view_character_and_generated_confirmed_state()
    {
        var missingCharacter = new[]
        {
            TitleTurn(),
            new DraftSegmentInput(1, SpeechSegmentSourceKind.Body, 0, 5, Hash("thought"), SpeechSegmentTurnKind.InnerMonologue, null, 100, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed),
        };
        var invalidGeneratedState = new[]
        {
            TitleTurn(),
            new DraftSegmentInput(1, SpeechSegmentSourceKind.Body, 0, 5, Hash("thought-review"), SpeechSegmentTurnKind.InnerMonologue, AliceId, 99, SpeechSegmentDecisionSource.LocalModel, SpeechSegmentReviewStatus.Suggested),
        };

        Assert.Throws<ArgumentException>(() => CreateDraft(missingCharacter));
        Assert.Throws<ArgumentException>(() => CreateDraft(invalidGeneratedState));
    }

    [Fact]
    public void Draft_needs_review_while_any_dialogue_segment_is_unconfirmed_and_is_ready_once_all_confirmed()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);

        Assert.Equal(ChapterSpeechPlanDraftStatus.NeedsReview, draft.Status);

        draft.ConfirmSegment(draft.Segments[1].Id, AliceId);

        Assert.Equal(ChapterSpeechPlanDraftStatus.ReadyToConfirm, draft.Status);
    }

    [Fact]
    public void Draft_with_only_narrator_segments_is_ready_to_confirm_immediately()
    {
        var draft = CreateDraft([TitleTurn()]);

        Assert.Equal(ChapterSpeechPlanDraftStatus.ReadyToConfirm, draft.Status);
    }

    [Fact]
    public void Confirmed_inner_monologue_does_not_create_a_speaker_review_gap()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            new DraftSegmentInput(1, SpeechSegmentSourceKind.Body, 0, 5, Hash("thought"), SpeechSegmentTurnKind.InnerMonologue, AliceId, 100, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed),
        ]);

        Assert.Equal(ChapterSpeechPlanDraftStatus.ReadyToConfirm, draft.Status);
        Assert.Equal(AliceId, draft.Segments[1].CharacterId);
    }

    [Fact]
    public void Confirming_a_segment_locks_in_a_possibly_different_character_and_marks_it_user_decided()
    {
        var bobId = Guid.NewGuid();
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);

        draft.ConfirmSegment(draft.Segments[1].Id, bobId);

        var segment = draft.Segments[1];
        Assert.Equal(bobId, segment.CharacterId);
        Assert.Equal(SpeechSegmentReviewStatus.Confirmed, segment.ReviewStatus);
        Assert.Equal(SpeechSegmentDecisionSource.User, segment.DecisionSource);
        Assert.Equal(100, segment.Confidence);
    }

    [Fact]
    public void Confirming_a_segment_to_null_records_an_explicit_narrator_fallback()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);

        draft.ConfirmSegment(draft.Segments[1].Id, null);

        Assert.Null(draft.Segments[1].CharacterId);
        Assert.Equal(SpeechSegmentReviewStatus.Confirmed, draft.Segments[1].ReviewStatus);
        Assert.Equal(ChapterSpeechPlanDraftStatus.ReadyToConfirm, draft.Status);
    }

    [Fact]
    public void Rejecting_a_segment_clears_the_suggestion_and_keeps_the_draft_in_review()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);

        draft.RejectSegment(draft.Segments[1].Id);

        Assert.Null(draft.Segments[1].CharacterId);
        Assert.Equal(SpeechSegmentReviewStatus.Rejected, draft.Segments[1].ReviewStatus);
        Assert.Equal(ChapterSpeechPlanDraftStatus.NeedsReview, draft.Status);
    }

    [Fact]
    public void Narrator_segments_cannot_be_confirmed_or_rejected_by_a_human()
    {
        var draft = CreateDraft([TitleTurn()]);

        Assert.Throws<InvalidOperationException>(() => draft.ConfirmSegment(draft.Segments[0].Id, null));
        Assert.Throws<InvalidOperationException>(() => draft.RejectSegment(draft.Segments[0].Id));
    }

    [Fact]
    public void Stale_draft_rejects_review_until_regenerated()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);
        draft.MarkStale();

        Assert.Equal(ChapterSpeechPlanDraftStatus.Stale, draft.Status);
        Assert.Throws<InvalidOperationException>(() => draft.ConfirmSegment(draft.Segments[1].Id, AliceId));

        draft.RegenerateFromSegmentation(Hash("chapter-v2"), [TitleTurn()]);

        Assert.Equal(ChapterSpeechPlanDraftStatus.ReadyToConfirm, draft.Status);
        Assert.Equal(2, draft.PlanVersion);
    }

    [Fact]
    public void Regenerating_matching_dialogue_preserves_user_confirmed_and_rejected_decisions()
    {
        var confirmedCharacterId = Guid.NewGuid();
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
            Dialogue(2, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);
        draft.ConfirmSegment(draft.Segments[1].Id, confirmedCharacterId);
        draft.RejectSegment(draft.Segments[2].Id);

        draft.RegenerateFromSegmentation(
            Hash("chapter-v2"),
            [
                TitleTurn(),
                Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
                Dialogue(2, AliceId, SpeechSegmentReviewStatus.Suggested),
            ]);

        var confirmed = draft.Segments[1];
        Assert.Equal(confirmedCharacterId, confirmed.CharacterId);
        Assert.Equal(100, confirmed.Confidence);
        Assert.Equal(SpeechSegmentDecisionSource.User, confirmed.DecisionSource);
        Assert.Equal(SpeechSegmentReviewStatus.Confirmed, confirmed.ReviewStatus);

        var rejected = draft.Segments[2];
        Assert.Null(rejected.CharacterId);
        Assert.Equal(0, rejected.Confidence);
        Assert.Equal(SpeechSegmentDecisionSource.User, rejected.DecisionSource);
        Assert.Equal(SpeechSegmentReviewStatus.Rejected, rejected.ReviewStatus);
        Assert.Equal(ChapterSpeechPlanDraftStatus.NeedsReview, draft.Status);
    }

    [Fact]
    public void Regenerating_changed_dialogue_does_not_carry_a_user_decision_to_new_text()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);
        draft.ConfirmSegment(draft.Segments[1].Id, AliceId);
        var changedDialogue = Dialogue(1, null, SpeechSegmentReviewStatus.Suggested) with
        {
            TextHash = Hash("changed-dialogue"),
        };

        draft.RegenerateFromSegmentation(Hash("chapter-v2"), [TitleTurn(), changedDialogue]);

        var regenerated = draft.Segments[1];
        Assert.Null(regenerated.CharacterId);
        Assert.Equal(SpeechSegmentDecisionSource.Rule, regenerated.DecisionSource);
        Assert.Equal(SpeechSegmentReviewStatus.Suggested, regenerated.ReviewStatus);
    }

    [Fact]
    public void Confirm_throws_unless_the_draft_is_ready_and_produces_an_immutable_revision_matching_segments()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);

        Assert.Throws<InvalidOperationException>(() => draft.Confirm(1, DateTimeOffset.UtcNow));

        draft.ConfirmSegment(draft.Segments[1].Id, AliceId);
        var revision = draft.Confirm(1, DateTimeOffset.UtcNow);

        Assert.Equal(OwnerId, revision.OwnerId);
        Assert.Equal(SeriesId, revision.SeriesId);
        Assert.Equal(BookId, revision.BookId);
        Assert.Equal(ChapterId, revision.ChapterId);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal(draft.SourceHash, revision.SourceHash);
        Assert.Equal(2, revision.Segments.Count);
        Assert.Equal(AliceId, revision.Segments[1].CharacterId);
        Assert.False(string.IsNullOrWhiteSpace(revision.Fingerprint));
        Assert.True(revision.MatchesDraft(draft));

        draft.RegenerateFromSegmentation(draft.SourceHash, [TitleTurn()]);

        Assert.False(revision.MatchesDraft(draft));
    }

    [Fact]
    public void Same_confirmed_segments_produce_the_same_fingerprint_and_any_field_change_produces_a_different_one()
    {
        ConfirmedSpeechPlanRevision Build(Guid? characterId)
        {
            var draft = CreateDraft(
            [
                TitleTurn(),
                Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
            ]);
            draft.ConfirmSegment(draft.Segments[1].Id, characterId);
            return draft.Confirm(1, DateTimeOffset.UtcNow);
        }

        var first = Build(AliceId);
        var second = Build(AliceId);
        var differentCharacter = Build(Guid.NewGuid());

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.Fingerprint, differentCharacter.Fingerprint);
    }

    [Fact]
    public void Confirming_suggested_segments_accepts_only_suggestions_that_carry_a_character()
    {
        var bobId = Guid.NewGuid();
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
            Dialogue(2, bobId, SpeechSegmentReviewStatus.Suggested),
            Dialogue(3, null, SpeechSegmentReviewStatus.Suggested),
        ]);

        var confirmedCount = draft.ConfirmSuggestedSegments(characterId: null);

        Assert.Equal(2, confirmedCount);
        Assert.Equal(SpeechSegmentReviewStatus.Confirmed, draft.Segments[1].ReviewStatus);
        Assert.Equal(AliceId, draft.Segments[1].CharacterId);
        Assert.Equal(SpeechSegmentDecisionSource.User, draft.Segments[1].DecisionSource);
        Assert.Equal(SpeechSegmentReviewStatus.Confirmed, draft.Segments[2].ReviewStatus);
        Assert.Equal(SpeechSegmentReviewStatus.Suggested, draft.Segments[3].ReviewStatus);
        Assert.Equal(ChapterSpeechPlanDraftStatus.NeedsReview, draft.Status);
    }

    [Fact]
    public void Confirming_suggested_segments_for_one_character_leaves_other_suggestions_pending()
    {
        var bobId = Guid.NewGuid();
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
            Dialogue(2, bobId, SpeechSegmentReviewStatus.Suggested),
        ]);

        var confirmedCount = draft.ConfirmSuggestedSegments(AliceId);

        Assert.Equal(1, confirmedCount);
        Assert.Equal(SpeechSegmentReviewStatus.Confirmed, draft.Segments[1].ReviewStatus);
        Assert.Equal(SpeechSegmentReviewStatus.Suggested, draft.Segments[2].ReviewStatus);
    }

    [Fact]
    public void Reassigning_turns_a_misread_narrator_segment_into_confirmed_dialogue_and_back()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            new DraftSegmentInput(1, SpeechSegmentSourceKind.Body, 0, 5, Hash("misread"), SpeechSegmentTurnKind.Narrator, null, 100, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed),
        ]);

        draft.ReassignSegment(draft.Segments[1].Id, SpeechSegmentTurnKind.Dialogue, AliceId);

        var segment = draft.Segments[1];
        Assert.Equal(SpeechSegmentTurnKind.Dialogue, segment.Kind);
        Assert.Equal(AliceId, segment.CharacterId);
        Assert.Equal(SpeechSegmentDecisionSource.User, segment.DecisionSource);
        Assert.Equal(SpeechSegmentReviewStatus.Confirmed, segment.ReviewStatus);
        Assert.Equal(ChapterSpeechPlanDraftStatus.ReadyToConfirm, draft.Status);

        draft.ReassignSegment(segment.Id, SpeechSegmentTurnKind.Narrator, null);

        Assert.Equal(SpeechSegmentTurnKind.Narrator, draft.Segments[1].Kind);
        Assert.Null(draft.Segments[1].CharacterId);
    }

    [Fact]
    public void Reassigning_can_correct_an_auto_confirmed_rule_decision()
    {
        var bobId = Guid.NewGuid();
        var draft = CreateDraft(
        [
            TitleTurn(),
            new DraftSegmentInput(1, SpeechSegmentSourceKind.Body, 10, 6, Hash("auto"), SpeechSegmentTurnKind.Dialogue, AliceId, 92, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed),
        ]);

        draft.ReassignSegment(draft.Segments[1].Id, SpeechSegmentTurnKind.Dialogue, bobId);

        Assert.Equal(bobId, draft.Segments[1].CharacterId);
        Assert.Equal(SpeechSegmentDecisionSource.User, draft.Segments[1].DecisionSource);
    }

    [Fact]
    public void Reassigning_enforces_kind_invariants_and_rejects_chapter_title_segments()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);

        Assert.Throws<InvalidOperationException>(
            () => draft.ReassignSegment(draft.Segments[0].Id, SpeechSegmentTurnKind.Dialogue, AliceId));
        Assert.Throws<ArgumentException>(
            () => draft.ReassignSegment(draft.Segments[1].Id, SpeechSegmentTurnKind.Narrator, AliceId));
        Assert.Throws<ArgumentException>(
            () => draft.ReassignSegment(draft.Segments[1].Id, SpeechSegmentTurnKind.InnerMonologue, null));
    }

    [Fact]
    public void Stale_draft_rejects_bulk_confirmation_and_reassignment()
    {
        var draft = CreateDraft(
        [
            TitleTurn(),
            Dialogue(1, AliceId, SpeechSegmentReviewStatus.Suggested),
        ]);
        draft.MarkStale();

        Assert.Throws<InvalidOperationException>(() => draft.ConfirmSuggestedSegments(null));
        Assert.Throws<InvalidOperationException>(
            () => draft.ReassignSegment(draft.Segments[1].Id, SpeechSegmentTurnKind.Narrator, null));
    }

    private static ChapterSpeechPlanDraft CreateDraft(IReadOnlyList<DraftSegmentInput> segments) =>
        ChapterSpeechPlanDraft.Create(OwnerId, SeriesId, BookId, ChapterId, Hash("chapter-v1"), segments);

    private static DraftSegmentInput TitleTurn() =>
        new(0, SpeechSegmentSourceKind.ChapterTitle, 0, 4, Hash("title"), SpeechSegmentTurnKind.Narrator, null, 100, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed);

    private static DraftSegmentInput PointOfViewTitleTurn() =>
        new(0, SpeechSegmentSourceKind.ChapterTitle, 0, 4, Hash("title"), SpeechSegmentTurnKind.InnerMonologue, AliceId, 100, SpeechSegmentDecisionSource.Rule, SpeechSegmentReviewStatus.Confirmed);

    private static DraftSegmentInput Dialogue(int sortOrder, Guid? characterId, SpeechSegmentReviewStatus reviewStatus) =>
        new(sortOrder, SpeechSegmentSourceKind.Body, 10, 6, Hash($"dialogue-{sortOrder}"), SpeechSegmentTurnKind.Dialogue, characterId, 60, SpeechSegmentDecisionSource.Rule, reviewStatus);

    private static string Hash(string seed) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
}

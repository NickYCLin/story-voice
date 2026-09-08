namespace StoryVoice.Application.Narrations;

public sealed record CreateNarrationRequest(bool RightsAttested);

public sealed record NarrationJobResponse(
    Guid Id,
    Guid BookId,
    Guid ContentBookId,
    string SourceHash,
    string Voice,
    string Rate,
    string Status,
    int ProgressPercent,
    int Attempts,
    bool CancellationRequested,
    string? ErrorCode,
    long? AudioBytes,
    DateTimeOffset RightsAttestedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record NarrationAudioDescriptor(string AbsolutePath, string ContentType);

public sealed record SaveListeningProgressRequest(long PositionMs, long DurationMs, Guid? ExpectedVersion);

public sealed record ListeningProgressResponse(
    Guid JobId, long PositionMs, long DurationMs, Guid? Version, DateTimeOffset? UpdatedAt);

public sealed record SaveListeningProgressResult(ListeningProgressResponse Progress, bool Conflict);

/// <summary>
/// The playback timeline for one completed narration job. <see cref="TextAvailable"/> is false
/// when the book's chapters changed after the audio was composed — timing and chapter navigation
/// still work, but stale offsets are never sliced into wrong text.
/// </summary>
public sealed record NarrationTimelineResponse(
    Guid JobId,
    bool TextAvailable,
    IReadOnlyList<NarrationTimelineChapterResponse> Chapters,
    IReadOnlyList<NarrationTimelineTurnResponse> Turns);

public sealed record NarrationTimelineChapterResponse(
    Guid ChapterId,
    int SortOrder,
    string Title,
    long StartMs);

public sealed record NarrationTimelineTurnResponse(
    int Index,
    long StartMs,
    long DurationMs,
    int ChapterSortOrder,
    string Kind,
    Guid? CharacterId,
    string? CharacterName,
    string? Text);

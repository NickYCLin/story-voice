using System.Text.Json;

namespace StoryVoice.Application.Narrations;

/// <summary>
/// The durable JSON document stored in <c>narration_timelines</c>. It carries only timing and
/// offsets into the job's locked chapter text (title or body), never the text itself — the read
/// side re-verifies the job's source hash before slicing text back out of the current chapters.
/// </summary>
public sealed record NarrationTimelineDocument(
    string SchemaVersion,
    IReadOnlyList<NarrationTimelineTurn> Turns)
{
    public const string CurrentSchemaVersion = "storyvoice:narration-timeline:v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static NarrationTimelineDocument? FromJson(string json)
    {
        NarrationTimelineDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<NarrationTimelineDocument>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null
            || !string.Equals(document.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal)
            || document.Turns is null
            || document.Turns.Count == 0
            || document.Turns.Any(turn => turn is null
                || turn.StartMs < 0
                || turn.DurationMs < 0
                || turn.Slices is null
                || turn.Slices.Count == 0
                || turn.Slices.Any(slice => slice is null || slice.StartOffset < 0 || slice.Length < 1)))
        {
            return null;
        }

        return document;
    }
}

/// <summary>One synthesized turn: where its audible audio starts inside the published MP3, how
/// long it plays, which chapter it belongs to, and the exact text slices it was built from.</summary>
public sealed record NarrationTimelineTurn(
    long StartMs,
    long DurationMs,
    Guid ChapterId,
    int ChapterSortOrder,
    bool ChapterStart,
    IReadOnlyList<NarrationTimelineSlice> Slices);

/// <summary>A single confirmed-segment slice folded into a turn. <see cref="SourceKind"/> selects
/// chapter title vs body; <see cref="Kind"/> mirrors the confirmed segment's turn kind.</summary>
public sealed record NarrationTimelineSlice(
    string SourceKind,
    int StartOffset,
    int Length,
    string Kind,
    Guid? CharacterId);

public static class NarrationTimelineSourceKinds
{
    public const string ChapterTitle = "title";
    public const string ChapterBody = "body";
}

public static class NarrationTimelineTurnKinds
{
    public const string Narrator = "narrator";
    public const string Dialogue = "dialogue";
    public const string InnerMonologue = "innerMonologue";
    public const string Mixed = "mixed";
}

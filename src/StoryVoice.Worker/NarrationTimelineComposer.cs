using StoryVoice.Application.Narrations;
using StoryVoice.Domain.Narrations;

namespace StoryVoice.Worker;

/// <summary>
/// Joins the turn plan's story-side provenance with the provider-reported per-turn timings into
/// the durable timeline document. Returns null whenever the provider could not report timing (or
/// reported something inconsistent) — the job still completes, just without playback sync.
/// </summary>
internal static class NarrationTimelineComposer
{
    public static NarrationTimelineDocument? Compose(
        IReadOnlyList<NarrationTurnSource> sources,
        IReadOnlyList<NarrationTurnTiming>? timings)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (timings is null || timings.Count == 0 || timings.Count != sources.Count)
        {
            return null;
        }

        var turns = new List<NarrationTimelineTurn>(sources.Count);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var timing = timings[index];
            if (timing.TurnIndex != index)
            {
                return null;
            }

            turns.Add(new NarrationTimelineTurn(
                timing.StartMs,
                timing.DurationMs,
                source.ChapterId,
                source.ChapterSortOrder,
                source.ChapterStart,
                source.Slices
                    .Select(slice => new NarrationTimelineSlice(
                        MapSourceKind(slice.SourceKind),
                        slice.StartOffset,
                        slice.Length,
                        MapTurnKind(slice.Kind),
                        slice.CharacterId))
                    .ToArray()));
        }

        return new NarrationTimelineDocument(NarrationTimelineDocument.CurrentSchemaVersion, turns);
    }

    private static string MapSourceKind(SpeechSegmentSourceKind sourceKind) => sourceKind switch
    {
        SpeechSegmentSourceKind.ChapterTitle => NarrationTimelineSourceKinds.ChapterTitle,
        SpeechSegmentSourceKind.Body => NarrationTimelineSourceKinds.ChapterBody,
        _ => throw new ArgumentOutOfRangeException(nameof(sourceKind)),
    };

    private static string MapTurnKind(SpeechSegmentTurnKind kind) => kind switch
    {
        SpeechSegmentTurnKind.Narrator => NarrationTimelineTurnKinds.Narrator,
        SpeechSegmentTurnKind.Dialogue => NarrationTimelineTurnKinds.Dialogue,
        SpeechSegmentTurnKind.InnerMonologue => NarrationTimelineTurnKinds.InnerMonologue,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

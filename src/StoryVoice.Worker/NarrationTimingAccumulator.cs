namespace StoryVoice.Worker;

/// <summary>Groups measured chunk durations into turns, rounding only the cumulative boundaries.</summary>
internal sealed class NarrationTimingAccumulator
{
    private readonly List<NarrationTurnTiming> _turns = [];
    private decimal _cursorMs;
    private bool _valid = true;

    public void Add(int turnIndex, double durationSeconds, double pauseBeforeMs)
    {
        if (!_valid) return;
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || durationSeconds > 2_592_000
            || !double.IsFinite(pauseBeforeMs) || pauseBeforeMs < 0 || pauseBeforeMs >= durationSeconds * 1000
            || turnIndex < 0 || turnIndex > _turns.Count || turnIndex < _turns.Count - 1)
        {
            _valid = false;
            return;
        }

        var startMs = (long)Math.Round(_cursorMs + (decimal)pauseBeforeMs, MidpointRounding.AwayFromZero);
        _cursorMs += (decimal)durationSeconds * 1000;
        var endMs = (long)Math.Round(_cursorMs, MidpointRounding.AwayFromZero);
        if (turnIndex == _turns.Count)
            _turns.Add(new(turnIndex, startMs, endMs - startMs));
        else
            _turns[turnIndex] = _turns[turnIndex] with { DurationMs = endMs - _turns[turnIndex].StartMs };
    }

    public MultiVoiceSynthesisResult ToResult(int turnCount) =>
        _valid && _turns.Count == turnCount && turnCount > 0 && _turns.All(turn => turn.DurationMs > 0)
            ? new(_turns.ToArray()) : MultiVoiceSynthesisResult.None;
}

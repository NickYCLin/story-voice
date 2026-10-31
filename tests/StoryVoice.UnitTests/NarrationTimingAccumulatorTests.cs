using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class NarrationTimingAccumulatorTests
{
    [Fact]
    public void Chunks_share_a_turn_and_leading_silence_is_excluded_from_its_audible_range()
    {
        var timing = new NarrationTimingAccumulator();
        timing.Add(0, 1.25, 250);
        timing.Add(0, 0.5, 0);
        timing.Add(1, 2.5, 500);
        Assert.Equal(new[] { new NarrationTurnTiming(0, 250, 1500), new(1, 2250, 2000) }, timing.ToResult(2).TurnTimings);
    }

    [Fact]
    public void Rounding_does_not_accumulate_half_a_millisecond_per_chunk_in_long_books()
    {
        var timing = new NarrationTimingAccumulator();
        for (var index = 0; index < 10_000; index++) timing.Add(index, 0.1004, 0);
        var turns = timing.ToResult(10_000).TurnTimings!;
        Assert.Equal(1_004_000, turns[^1].StartMs + turns[^1].DurationMs);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 1000)]
    public void Invalid_measurements_omit_the_timeline(double seconds, double pauseMs)
    {
        var timing = new NarrationTimingAccumulator();
        timing.Add(0, seconds, pauseMs);
        Assert.Null(timing.ToResult(1).TurnTimings);
    }

    [Fact]
    public void Missing_or_out_of_order_turns_omit_the_timeline()
    {
        var timing = new NarrationTimingAccumulator();
        timing.Add(1, 1, 0);
        Assert.Null(timing.ToResult(2).TurnTimings);
        var missing = new NarrationTimingAccumulator();
        missing.Add(0, 1, 0);
        Assert.Null(missing.ToResult(2).TurnTimings);
    }
}

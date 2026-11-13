using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

public sealed class Mp3PacketDurationTests
{
    [Fact]
    public void Packet_durations_include_padding_frames_and_ignore_unrequested_metadata()
    {
        Assert.Equal(0.078366, Mp3PacketDuration.ParseSeconds(
            "duration_time=0.026122\r\nskip_samples=1105\nduration_time=0.026122\nduration_time=0.026122\n"), 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("duration=1.25")]
    [InlineData("duration_time=N/A")]
    [InlineData("duration_time=NaN")]
    [InlineData("duration_time=Infinity")]
    [InlineData("duration_time=-0.026122")]
    [InlineData("duration_time=0")]
    [InlineData("duration_time=0.026122\nduration_time=N/A")]
    [InlineData("duration_time=2592000\nduration_time=0.026122")]
    public void Missing_invalid_or_excessive_packet_durations_cannot_produce_a_partial_measurement(string output) =>
        Assert.Equal(0, Mp3PacketDuration.ParseSeconds(output));
}

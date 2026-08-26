using System.Buffers.Binary;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.UnitTests;

public sealed class LocalClonePcmWaveValidatorTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public void ValidateReference_accepts_the_pinned_provider_duration_boundaries(
        int durationSeconds)
    {
        LocalClonePcmWaveValidator.ValidateReference(
            CreatePcmWav(checked(48_000 * durationSeconds)));
    }

    [Fact]
    public void ValidateReference_rejects_even_one_frame_over_the_30_second_provider_limit()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            LocalClonePcmWaveValidator.ValidateReference(
                CreatePcmWav(checked((48_000 * 30) + 1))));

        Assert.Contains("duration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateExternalOutput_returns_the_exact_pcm_duration_in_milliseconds()
    {
        var duration = LocalClonePcmWaveValidator.ValidateExternalOutput(
            CreatePcmWav(checked(24_000 * 2), sampleRate: 24_000),
            maximumBytes: 4 * 1024 * 1024);

        Assert.Equal(2_000, duration);
    }

    private static byte[] CreatePcmWav(int frames, uint sampleRate = 48_000)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        const ushort blockAlign = 2;
        var dataLength = checked(frames * blockAlign);
        var wav = new byte[checked(44 + dataLength)];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), checked((uint)(wav.Length - 8)));
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), sampleRate * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), bitsPerSample);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), checked((uint)dataLength));
        return wav;
    }
}

using System.Buffers.Binary;

namespace StoryVoice.Infrastructure.Narrations;

internal static class LocalClonePcmWaveValidator
{
    private const ushort PcmFormat = 1;

    public static void ValidateReference(ReadOnlySpan<byte> content) =>
        _ = Validate(
            content,
            requiredSampleRate: 48_000,
            minimumDurationSeconds: 10,
            maximumDurationSeconds: 30,
            maximumBytes: LocalClonePreviewOptions.MaximumReferenceAudioBytes);

    public static void ValidateOutput(ReadOnlySpan<byte> content, int maximumBytes) =>
        _ = Validate(
            content,
            requiredSampleRate: 24_000,
            minimumDurationSeconds: 0,
            maximumDurationSeconds: 300,
            maximumBytes);

    public static long ValidateExternalOutput(ReadOnlySpan<byte> content, int maximumBytes) =>
        Validate(
            content,
            requiredSampleRate: 24_000,
            minimumDurationSeconds: 0,
            maximumDurationSeconds: 60,
            maximumBytes);

    public static void ValidatePublicDemo(ReadOnlySpan<byte> content, int maximumBytes) =>
        _ = Validate(
            content,
            requiredSampleRate: 24_000,
            minimumDurationSeconds: 0,
            maximumDurationSeconds: 60,
            maximumBytes,
            rejectUnknownChunks: true);

    private static long Validate(
        ReadOnlySpan<byte> content,
        uint requiredSampleRate,
        double minimumDurationSeconds,
        double maximumDurationSeconds,
        int maximumBytes,
        bool rejectUnknownChunks = false)
    {
        const ushort requiredChannels = 1;
        const ushort requiredBitsPerSample = 16;
        const ushort requiredBlockAlign = 2;

        if (content.Length is < 44 || content.Length > maximumBytes
            || !content[..4].SequenceEqual("RIFF"u8)
            || !content.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Invalid PCM WAV container.");
        }

        var declaredLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(4, 4)) + 8;
        if (declaredLength != content.Length)
        {
            throw new InvalidDataException("Invalid RIFF length.");
        }

        var requiredByteRate = checked(requiredSampleRate * requiredBlockAlign);
        var foundFormat = false;
        long? dataBytes = null;
        var offset = 12;
        while (offset < content.Length)
        {
            if (content.Length - offset < 8)
            {
                throw new InvalidDataException("Incomplete WAV chunk header.");
            }

            var chunkId = content.Slice(offset, 4);
            var chunkSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(offset + 4, 4));
            offset += 8;
            var paddedChunkSize = checked(chunkSize + (chunkSize & 1));
            if (paddedChunkSize > content.Length - offset)
            {
                throw new InvalidDataException("Invalid WAV chunk length.");
            }

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (foundFormat || chunkSize < 16 || rejectUnknownChunks && chunkSize != 16)
                {
                    throw new InvalidDataException("Invalid PCM format chunk.");
                }

                var format = content.Slice(offset, 16);
                if (BinaryPrimitives.ReadUInt16LittleEndian(format) != PcmFormat
                    || BinaryPrimitives.ReadUInt16LittleEndian(format[2..]) != requiredChannels
                    || BinaryPrimitives.ReadUInt32LittleEndian(format[4..]) != requiredSampleRate
                    || BinaryPrimitives.ReadUInt32LittleEndian(format[8..]) != requiredByteRate
                    || BinaryPrimitives.ReadUInt16LittleEndian(format[12..]) != requiredBlockAlign
                    || BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) != requiredBitsPerSample)
                {
                    throw new InvalidDataException("Unexpected PCM WAV format.");
                }

                foundFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (dataBytes is not null || chunkSize == 0 || chunkSize % requiredBlockAlign != 0)
                {
                    throw new InvalidDataException("Invalid PCM data chunk.");
                }

                dataBytes = chunkSize;
            }
            else if (rejectUnknownChunks)
            {
                throw new InvalidDataException("Public PCM WAV contains an unsupported metadata chunk.");
            }

            offset = checked(offset + (int)paddedChunkSize);
        }

        if (!foundFormat || dataBytes is null)
        {
            throw new InvalidDataException("Required PCM WAV chunks are missing.");
        }

        var durationSeconds = dataBytes.Value / (double)requiredByteRate;
        if (durationSeconds <= 0
            || durationSeconds < minimumDurationSeconds
            || durationSeconds > maximumDurationSeconds)
        {
            throw new InvalidDataException("PCM WAV duration is outside the accepted bounds.");
        }

        return checked((long)Math.Ceiling(durationSeconds * 1_000));
    }
}

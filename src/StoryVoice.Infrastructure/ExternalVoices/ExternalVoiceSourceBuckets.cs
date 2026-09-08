using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace StoryVoice.Infrastructure.ExternalVoices;

public static class ExternalVoiceSourceBuckets
{
    public const int Count = 256;

    public static int Resolve(IPAddress? sourceAddress, ReadOnlySpan<byte> hashKey)
    {
        Span<byte> normalized = stackalloc byte[17];
        var length = 1;
        normalized.Clear();
        if (sourceAddress is not null)
        {
            if (sourceAddress.IsIPv4MappedToIPv6) sourceAddress = sourceAddress.MapToIPv4();
            if (sourceAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                normalized[0] = 4;
                sourceAddress.TryWriteBytes(normalized[1..], out var written);
                length = written + 1;
            }
            else
            {
                normalized[0] = 6;
                sourceAddress.TryWriteBytes(normalized[1..], out _);
                // IPv6 privacy addresses share their /64 network budget.
                normalized[9..].Clear();
                length = normalized.Length;
            }
        }

        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(hashKey, normalized[..length], digest);
        return (int)(BinaryPrimitives.ReadUInt32LittleEndian(digest) % Count);
    }
}

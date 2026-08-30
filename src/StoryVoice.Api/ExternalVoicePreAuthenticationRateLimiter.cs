using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using StoryVoice.Infrastructure.ExternalVoices;

namespace StoryVoice.Api;

public sealed class ExternalVoicePreAuthenticationRateLimiter : IDisposable
{
    // Hashing normalized source networks into a fixed array gives the anti-abuse
    // guard a hard memory bound even when an attacker rotates source addresses.
    internal const int SourceBucketCount = 256;

    private readonly byte[] hashSalt = RandomNumberGenerator.GetBytes(32);
    private readonly FixedWindowRateLimiter[] sourceLimiters;
    private readonly FixedWindowRateLimiter globalLimiter;

    public ExternalVoicePreAuthenticationRateLimiter(IOptions<ExternalVoiceApiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        sourceLimiters = Enumerable.Range(0, SourceBucketCount)
            .Select(_ => CreateLimiter(options.Value.PreAuthenticationRequestsPerMinute))
            .ToArray();
        globalLimiter = CreateLimiter(options.Value.PreAuthenticationGlobalRequestsPerMinute);
    }

    public bool TryAcquire(IPAddress? sourceAddress, out int retryAfterSeconds)
    {
        var sourceLimiter = sourceLimiters[ResolveSourceBucket(sourceAddress)];
        sourceLimiter.TryReplenish();
        using var sourceLease = sourceLimiter.AttemptAcquire(permitCount: 1);
        if (!sourceLease.IsAcquired)
        {
            retryAfterSeconds = ResolveRetryAfterSeconds(sourceLease);
            return false;
        }

        globalLimiter.TryReplenish();
        using var globalLease = globalLimiter.AttemptAcquire(permitCount: 1);
        if (!globalLease.IsAcquired)
        {
            retryAfterSeconds = ResolveRetryAfterSeconds(globalLease);
            return false;
        }

        retryAfterSeconds = 0;
        return true;
    }

    internal int ResolveSourceBucket(IPAddress? sourceAddress)
    {
        Span<byte> normalized = stackalloc byte[17];
        var length = NormalizeSource(sourceAddress, normalized);
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(hashSalt, normalized[..length], digest);
        return (int)(BinaryPrimitives.ReadUInt32LittleEndian(digest) % SourceBucketCount);
    }

    private static int NormalizeSource(IPAddress? sourceAddress, Span<byte> destination)
    {
        if (sourceAddress is null)
        {
            destination[0] = 0;
            return 1;
        }

        if (sourceAddress.IsIPv4MappedToIPv6)
        {
            sourceAddress = sourceAddress.MapToIPv4();
        }

        if (sourceAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            destination[0] = 4;
            sourceAddress.TryWriteBytes(destination[1..], out var written);
            return written + 1;
        }

        destination[0] = 6;
        sourceAddress.TryWriteBytes(destination[1..], out _);
        // Native IPv6 clients are grouped by /64 so rotating privacy addresses
        // cannot manufacture a fresh bucket for every request.
        destination[9..17].Clear();
        return destination.Length;
    }

    private static FixedWindowRateLimiter CreateLimiter(int permitLimit) =>
        new(new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitLimit),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = false,
        });

    private static int ResolveRetryAfterSeconds(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Clamp(Math.Ceiling(retryAfter.TotalSeconds), 1, 60)
            : 60;

    public void Dispose()
    {
        foreach (var limiter in sourceLimiters)
        {
            limiter.Dispose();
        }

        globalLimiter.Dispose();
    }
}

internal sealed class ExternalVoicePreAuthenticationRateLimitMiddleware(
    RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        IOptions<ExternalVoiceApiOptions> options,
        ExternalVoicePreAuthenticationRateLimiter rateLimiter)
    {
        if (!options.Value.Enabled
            || !HttpMethods.IsPost(httpContext.Request.Method)
            || !string.Equals(
                httpContext.Request.Path.Value,
                "/api/external/v1/speech",
                StringComparison.Ordinal))
        {
            await next(httpContext);
            return;
        }

        if (rateLimiter.TryAcquire(
                httpContext.Connection.RemoteIpAddress,
                out var retryAfterSeconds))
        {
            await next(httpContext);
            return;
        }

        httpContext.Response.Headers.RetryAfter =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await ExternalVoiceEndpoints.WriteProblemAsync(
            httpContext,
            StatusCodes.Status429TooManyRequests,
            "Rate limit exceeded",
            "The external voice pre-authentication request limit was reached.",
            "rate_limited",
            httpContext.RequestAborted);
    }
}

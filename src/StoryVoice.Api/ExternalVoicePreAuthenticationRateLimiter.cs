using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Infrastructure.ExternalVoices;

namespace StoryVoice.Api;

public sealed class ExternalVoicePreAuthenticationRateLimiter : IDisposable
{
    // Hashing normalized source networks into a fixed array gives the anti-abuse
    // guard a hard memory bound even when an attacker rotates source addresses.
    internal const int SourceBucketCount = ExternalVoiceSourceBuckets.Count;

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

    internal int ResolveSourceBucket(IPAddress? sourceAddress) => ExternalVoiceSourceBuckets.Resolve(sourceAddress, hashSalt);

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
        ExternalVoicePreAuthenticationRateLimiter rateLimiter,
        IExternalVoiceSharedRateLimiter sharedRateLimiter)
    {
        if (!options.Value.Enabled
            || !HttpMethods.IsPost(httpContext.Request.Method)
            || httpContext.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
                != ExternalVoiceEndpoints.EndpointName)
        {
            await next(httpContext);
            return;
        }

        if (rateLimiter.TryAcquire(
                httpContext.Connection.RemoteIpAddress,
                out var retryAfterSeconds))
        {
            try
            {
                await sharedRateLimiter.EnsurePreAuthenticationAllowedAsync(
                    httpContext.Connection.RemoteIpAddress, httpContext.RequestAborted);
            }
            catch (ExternalVoiceSynthesisException exception)
            {
                var limited = exception.FailureKind == ExternalVoiceSynthesisFailureKind.RateLimited;
                httpContext.Response.Headers.RetryAfter = (exception.RetryAfterSeconds ?? 30).ToString(CultureInfo.InvariantCulture);
                await ExternalVoiceEndpoints.WriteProblemAsync(
                    httpContext,
                    limited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status503ServiceUnavailable,
                    limited ? "Rate limit exceeded" : "Service unavailable",
                    limited ? "The external voice pre-authentication request limit was reached." : "External voice requests are temporarily unavailable.",
                    limited ? "rate_limited" : "synthesis_unavailable",
                    httpContext.RequestAborted);
                return;
            }

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

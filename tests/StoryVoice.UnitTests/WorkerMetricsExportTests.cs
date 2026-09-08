using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using StoryVoice.Worker;

namespace StoryVoice.UnitTests;

[CollectionDefinition("Worker metrics export", DisableParallelization = true)]
public sealed class WorkerMetricsExportCollection;

[Collection("Worker metrics export")]
public sealed class WorkerMetricsExportTests
{
    [Fact]
    public async Task Disabled_export_does_not_register_a_pipeline_or_send_metrics()
    {
        await using var receiver = await Receiver.StartAsync();
        using var host = CreateHost(receiver.Endpoint, enabled: false);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var metrics = host.Services.GetRequiredService<BlueMagpieNarrationMetrics>();
        metrics.StartAttempt();
        metrics.FinishAttempt(TimeSpan.FromSeconds(2), "success", 4, false);
        Assert.Null(host.Services.GetService<MeterProvider>());
        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(receiver.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/v1/metrics")]
    [InlineData("ftp://collector.invalid/v1/metrics")]
    [InlineData("https://user:synthetic-secret@collector.invalid/v1/metrics")]
    [InlineData("https://collector.invalid/v1/metrics?token=synthetic-secret")]
    [InlineData("https://collector.invalid/v1/metrics#fragment")]
    [InlineData("http://collector.invalid:4318/")]
    public void Enabled_export_requires_an_explicit_safe_metrics_endpoint(string endpoint)
    {
        var error = Assert.Throws<OptionsValidationException>(() => CreateHost(endpoint));
        Assert.DoesNotContain("synthetic-secret", error.Message);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(3601, 1)]
    [InlineData(30, 0)]
    [InlineData(30, 31)]
    [InlineData(5, 6)]
    public void Export_interval_and_timeout_are_bounded(int interval, int timeout) =>
        Assert.Throws<OptionsValidationException>(() => CreateHost("http://collector.invalid/v1/metrics", interval: interval, timeout: timeout));

    [Fact]
    public async Task Exports_actual_metrics_over_http_with_bounded_resources_and_cumulative_values()
    {
        var originalAttributes = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        var originalHeaders = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", "private_marker=synthetic-private-resource");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", "authorization=synthetic-private-header");
        try
        {
            await using var receiver = await Receiver.StartAsync();
            using var capture = new CaptureExporter();
            using var host = CreateHost(receiver.Endpoint, capture: capture);
            await host.StartAsync(TestContext.Current.CancellationToken);
            var metrics = host.Services.GetRequiredService<BlueMagpieNarrationMetrics>();
            using var unrelated = new Meter("StoryVoice.PrivateTest");
            unrelated.CreateCounter<long>("synthetic-private-counter").Add(1, new KeyValuePair<string, object?>("owner", "synthetic-private-owner"));
            metrics.StartAttempt();
            metrics.ResolveChunk(TimeSpan.FromSeconds(0.25), true, 1000);
            metrics.ResolveChunk(TimeSpan.FromSeconds(1.5), false, 2000);
            metrics.RecordProviderCall(TimeSpan.FromSeconds(1), "success");
            metrics.FinishAttempt(TimeSpan.FromSeconds(6), "success", 12, true);
            var provider = host.Services.GetRequiredService<MeterProvider>();
            Assert.True(provider.ForceFlush(5000));

            var request = Assert.Single(receiver.Requests);
            Assert.Equal("application/x-protobuf", request.ContentType);
            Assert.Equal("POST", request.Method);
            Assert.Null(request.Authorization);
            var encoded = Encoding.UTF8.GetString(request.Body);
            Assert.DoesNotContain("synthetic-private", encoded);
            Assert.Equal(9, capture.Values.Count);
            Assert.All(capture.Values.Keys, name =>
            {
                Assert.StartsWith("storyvoice.bluemagpie.", name);
                Assert.Contains(name, encoded);
            });
            Assert.Equal(1, capture.Values["storyvoice.bluemagpie.attempts"]);
            Assert.Equal(0, capture.Values["storyvoice.bluemagpie.active_attempts"]);
            Assert.Equal(2, capture.Values["storyvoice.bluemagpie.resolved_chunks"]);
            Assert.Equal(3000, capture.Values["storyvoice.bluemagpie.resolved_audio_bytes"]);
            Assert.Equal(0.5, capture.Values["storyvoice.bluemagpie.real_time_factor"]);
            Assert.Equal("storyvoice-worker", capture.Resources["service.name"]);
            Assert.True(Guid.TryParse(capture.Resources["service.instance.id"]?.ToString(), out _));
            Assert.All(capture.Resources.Keys, key => Assert.Contains(key, new[]
            {
                "service.name", "service.instance.id", "telemetry.sdk.name", "telemetry.sdk.language", "telemetry.sdk.version",
            }));
            Assert.All(capture.TagKeys, key => Assert.Contains(key, new[] { "cache", "outcome", "reused_cache" }));

            metrics.StartAttempt();
            metrics.FinishAttempt(TimeSpan.FromSeconds(1), "cancelled", null, false);
            Assert.True(provider.ForceFlush(5000));
            Assert.Equal(2, capture.Values["storyvoice.bluemagpie.attempts"]);
            Assert.Equal(2, receiver.Requests.Count);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", originalAttributes);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", originalHeaders);
        }
    }

    [Theory]
    [InlineData(503)]
    [InlineData(307)]
    public async Task Collector_failure_and_redirect_do_not_stop_the_worker_or_forward_to_another_path(int status)
    {
        await using var receiver = await Receiver.StartAsync(status);
        using var host = CreateHost(receiver.Endpoint, timeout: 1);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var metrics = host.Services.GetRequiredService<BlueMagpieNarrationMetrics>();
        var provider = host.Services.GetRequiredService<MeterProvider>();
        _ = provider.ForceFlush(5000);
        Assert.NotEmpty(receiver.Requests);
        Assert.Equal(0, receiver.RedirectedRequests);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        metrics.StartAttempt();
        metrics.FinishAttempt(TimeSpan.FromSeconds(2), "success", 4, false);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unresponsive_collector_is_cancelled_within_the_configured_timeout()
    {
        await using var receiver = await Receiver.StartAsync(stall: true);
        using var host = CreateHost(receiver.Endpoint, timeout: 1);
        await host.StartAsync(TestContext.Current.CancellationToken);
        _ = host.Services.GetRequiredService<BlueMagpieNarrationMetrics>();
        var flush = Task.Run(() => host.Services.GetRequiredService<MeterProvider>().ForceFlush(5000), TestContext.Current.CancellationToken);
        await receiver.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _ = await flush.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static IHost CreateHost(string endpoint, bool enabled = true, int interval = 3600, int timeout = 5, CaptureExporter? capture = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkerMetrics:Enabled"] = enabled.ToString(),
            ["WorkerMetrics:Endpoint"] = endpoint,
            ["WorkerMetrics:ExportIntervalSeconds"] = interval.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["WorkerMetrics:ExportTimeoutSeconds"] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Services.AddStoryVoiceWorkerMetrics(builder.Configuration);
        builder.Services.AddSingleton<BlueMagpieNarrationMetrics>();
        if (capture is not null)
            builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(new PeriodicExportingMetricReader(capture, 3_600_000)));
        return builder.Build();
    }

    private sealed class CaptureExporter : BaseExporter<Metric>
    {
        public Dictionary<string, double> Values { get; } = [];
        public Dictionary<string, object> Resources { get; private set; } = [];
        public HashSet<string> TagKeys { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Values.Clear();
            Resources = ParentProvider.GetResource().Attributes.ToDictionary();
            foreach (var metric in batch)
            {
                double sum = 0;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    foreach (var tag in point.Tags) TagKeys.Add(tag.Key);
                    sum += metric.MetricType switch
                    {
                        MetricType.LongSum => point.GetSumLong(),
                        MetricType.LongGauge => point.GetGaugeLastValueLong(),
                        MetricType.Histogram => point.GetHistogramSum(),
                        _ => throw new InvalidOperationException("Unexpected test metric type."),
                    };
                }
                Values[metric.Name] = sum;
            }
            return ExportResult.Success;
        }
    }

    private sealed record ReceivedRequest(string Method, string? ContentType, string? Authorization, byte[] Body);

    private sealed class Receiver(WebApplication app) : IAsyncDisposable
    {
        public string Endpoint => app.Urls.Single() + "/v1/metrics";
        public ConcurrentQueue<ReceivedRequest> Requests { get; } = [];
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RedirectedRequests;

        public static async Task<Receiver> StartAsync(int status = 200, bool stall = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var receiver = new Receiver(app);
            app.MapPost("/v1/metrics", async context =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                receiver.Requests.Enqueue(new ReceivedRequest(context.Request.Method, context.Request.ContentType,
                    context.Request.Headers.Authorization.FirstOrDefault(), body.ToArray()));
                if (stall)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                    catch (OperationCanceledException) { receiver.Cancelled.TrySetResult(); }
                    return;
                }
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/x-protobuf";
                if (status == 307) context.Response.Headers.Location = "/redirected";
            });
            app.MapPost("/redirected", () => { Interlocked.Increment(ref receiver.RedirectedRequests); return Results.Ok(); });
            await app.StartAsync(TestContext.Current.CancellationToken);
            return receiver;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

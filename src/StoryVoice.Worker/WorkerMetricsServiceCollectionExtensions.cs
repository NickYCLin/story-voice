using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace StoryVoice.Worker;

public static class WorkerMetricsServiceCollectionExtensions
{
    public static IServiceCollection AddStoryVoiceWorkerMetrics(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMetrics();
        var options = configuration.GetSection(WorkerMetricsOptions.SectionName)
            .Get<WorkerMetricsOptions>() ?? new WorkerMetricsOptions();
        // Export configuration is a startup snapshot. No SDK/exporter is registered while disabled.
        if (!options.Enabled) return services;

        var errors = new List<string>();
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(endpoint.Host) || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || !endpoint.AbsolutePath.EndsWith("/v1/metrics", StringComparison.Ordinal))
            errors.Add("WorkerMetrics endpoint must be an HTTP(S) URL ending in /v1/metrics, without credentials, query or fragment.");
        if (options.ExportIntervalSeconds is < 5 or > 3600)
            errors.Add("WorkerMetrics export interval must be between 5 and 3600 seconds.");
        if (options.ExportTimeoutSeconds is < 1 or > 30
            || options.ExportTimeoutSeconds > options.ExportIntervalSeconds)
            errors.Add("WorkerMetrics export timeout must be between 1 and 30 seconds and no longer than the interval.");
        if (errors.Count > 0)
            throw new OptionsValidationException(WorkerMetricsOptions.SectionName, typeof(WorkerMetricsOptions), errors);

        services.AddHttpClient("OtlpMetricExporter", client =>
                client.Timeout = TimeSpan.FromSeconds(options.ExportTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(_ => true);
        services.AddOpenTelemetry().WithMetrics(metrics =>
        {
            // Do not inherit resource attributes from the environment or subscribe to other meters.
            metrics.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("storyvoice-worker"))
                .AddMeter(BlueMagpieNarrationMetrics.MeterName)
                .SetExemplarFilter(ExemplarFilterType.AlwaysOff);
            foreach (var instrument in new[] { "attempt.duration", "chunk.duration", "provider.duration", "rendered_audio.duration" })
                metrics.AddView($"storyvoice.bluemagpie.{instrument}", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 1800, 3600, 10800],
                });
            metrics.AddView("storyvoice.bluemagpie.real_time_factor", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10],
            });
            metrics.AddOtlpExporter((exporter, reader) =>
            {
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                exporter.Endpoint = endpoint!;
                exporter.Headers = "";
                exporter.TimeoutMilliseconds = options.ExportTimeoutSeconds * 1000;
                reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = options.ExportIntervalSeconds * 1000;
                reader.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = options.ExportTimeoutSeconds * 1000;
            });
        });
        return services;
    }
}

using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StoryVoice.Application.ExternalVoices;

namespace StoryVoice.Infrastructure.ExternalVoices;

/// <summary>
/// Owns the process-local, bounded best-effort usage queue. The API registers the same singleton
/// as both <see cref="IExternalVoiceUsageRecorder"/> and an <see cref="IHostedService"/>.
/// </summary>
public sealed class ExternalVoiceUsageBackgroundQueue : BackgroundService, IExternalVoiceUsageRecorder
{
    public const string MeterName = "StoryVoice.ExternalVoices";

    private static readonly Meter UsageMeter = new(MeterName);
    private static readonly Counter<long> DroppedRecords = UsageMeter.CreateCounter<long>(
        "storyvoice.external_voice_usage.dropped",
        unit: "{record}",
        description: "Usage ledger records dropped before durable persistence.");
    private static readonly Counter<long> PersistedRecords = UsageMeter.CreateCounter<long>(
        "storyvoice.external_voice_usage.persisted",
        unit: "{record}",
        description: "Usage ledger records durably persisted by the background queue.");
    private static readonly Counter<long> PersistenceFailures = UsageMeter.CreateCounter<long>(
        "storyvoice.external_voice_usage.persistence_failures",
        unit: "{record}",
        description: "Usage ledger records that failed during background persistence.");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExternalVoiceUsageBackgroundQueue> _logger;
    private readonly Channel<ExternalVoiceUsageWrite> _channel;
    private readonly int _capacity;

    public ExternalVoiceUsageBackgroundQueue(
        IServiceScopeFactory scopeFactory,
        IOptions<ExternalVoiceApiOptions> options,
        ILogger<ExternalVoiceUsageBackgroundQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
        _capacity = options.Value.UsageLedgerQueueCapacity;
        if (_capacity is < 1 or > ExternalVoiceApiOptions.MaximumUsageLedgerQueueCapacity)
        {
            throw new OptionsValidationException(
                ExternalVoiceApiOptions.SectionName,
                typeof(ExternalVoiceApiOptions),
                ["External voice usage ledger queue must contain between 1 and 10000 entries."]);
        }

        _channel = Channel.CreateBounded<ExternalVoiceUsageWrite>(new BoundedChannelOptions(_capacity)
        {
            // TryWrite with Wait mode returns false when full. DropWrite can report success even
            // when it discards the new item, which would hide ledger loss from logs and metrics.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public bool TryEnqueue(ExternalVoiceUsageWrite usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (_channel.Writer.TryWrite(usage))
        {
            return true;
        }

        DroppedRecords.Add(1, new KeyValuePair<string, object?>("reason", "queue_full"));
        _logger.LogWarning(
            "Dropped external voice usage record {RequestId} because the bounded queue reached capacity {Capacity}.",
            usage.RequestId,
            _capacity);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var usage in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // Never capture a request scope or its DbContext. Every queued item receives a
                    // new async scope, and a failed SaveChanges cannot poison the next item.
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var usageService = scope.ServiceProvider
                        .GetRequiredService<ExternalVoiceUsageService>();
                    await usageService.RecordAsync(usage, stoppingToken);
                    PersistedRecords.Add(1);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    PersistenceFailures.Add(1);
                    _logger.LogError(
                        exception,
                        "Failed to persist queued external voice usage record {RequestId}; processing will continue.",
                        usage.RequestId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Pending records are intentionally best-effort during process shutdown.
        }
    }
}

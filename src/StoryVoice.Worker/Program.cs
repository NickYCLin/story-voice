using Serilog;
using StoryVoice.Infrastructure;
using StoryVoice.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());
builder.Services.AddStoryVoiceInfrastructure(builder.Configuration);
builder.Services.AddOptions<BlueMagpieChunkCacheOptions>()
    .Bind(builder.Configuration.GetSection(BlueMagpieChunkCacheOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath),
        "BlueMagpie chunk cache root is required.")
    .Validate(options => options.MaximumBytes is >= 67_108_864 and <= 8_796_093_022_208,
        "BlueMagpie chunk cache maximum must be between 64 MiB and 8 TiB.")
    .Validate(options => options.LowWatermarkBytes >= 0
            && options.LowWatermarkBytes < options.MaximumBytes,
        "BlueMagpie chunk cache low watermark must be below its hard maximum.")
    .Validate(options => options.MinimumFreeBytes is >= 0 and <= 8_796_093_022_208,
        "BlueMagpie chunk cache free-space floor must be between 0 and 8 TiB.")
    .Validate(options => options.RetentionHours is >= 1 and <= 8_760,
        "BlueMagpie chunk cache retention must be between 1 hour and 1 year.")
    .Validate(options => options.CleanupIntervalMinutes is >= 1 and <= 1_440,
        "BlueMagpie chunk cache cleanup interval must be between 1 minute and 1 day.")
    .Validate(options => options.TemporaryEntryRetentionMinutes is >= 1 and <= 1_440,
        "BlueMagpie temporary cache retention must be between 1 minute and 1 day.")
    .Validate(options => options.LockRetryMilliseconds is >= 25 and <= 2_000,
        "BlueMagpie cache lock retry must be between 25 and 2000 milliseconds.")
    .ValidateOnStart();
builder.Services.AddOptions<VoAiOptions>()
    .Bind(builder.Configuration.GetSection(VoAiOptions.SectionName))
    .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
        && baseUri.Scheme == Uri.UriSchemeHttps
        && string.Equals(baseUri.Host, "connect.voai.ai", StringComparison.OrdinalIgnoreCase)
        && baseUri.IsDefaultPort,
        "VoAI base URL must use the official HTTPS origin https://connect.voai.ai.")
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 600,
        "VoAI timeout must be between 1 and 600 seconds.")
    .Validate(options => options.MaximumResponseBytes is >= 1_048_576 and <= 134_217_728,
        "VoAI response limit must be between 1 MiB and 128 MiB.")
    .Validate(options => options.MaximumJobResponseBytes is >= 67_108_864 and <= 8_589_934_592,
        "VoAI aggregate job response limit must be between 64 MiB and 8 GiB.")
    .Validate(options => options.MaximumChunksPerJob is >= 1 and <= 10_000,
        "VoAI chunks per job must be between 1 and 10000.")
    .Validate(options => options.SampleRate == 32_000,
        "VoAI synthesis currently requires a 32000 Hz WAV response.")
    .ValidateOnStart();
builder.Services.AddOptions<AudioComposerOptions>()
    .Bind(builder.Configuration.GetSection(AudioComposerOptions.SectionName))
    .Validate(options => options.TargetIntegratedLoudness is >= -70.0 and <= -5.0,
        "Target integrated loudness must be between -70.0 and -5.0 LUFS.")
    .Validate(options => options.TargetTruePeak is >= -9.0 and <= 0.0,
        "Target true peak must be between -9.0 and 0.0 dBFS.")
    .Validate(options => options.TargetLoudnessRange is >= 1.0 and <= 50.0,
        "Target loudness range must be between 1.0 and 50.0 LU.")
    .ValidateOnStart();
builder.Services.AddHttpClient(VoAiTtsClient.HttpClientName, client =>
        client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
    })
    .RedactLoggedHeaders(headerName =>
        string.Equals(headerName, "x-api-key", StringComparison.OrdinalIgnoreCase));
builder.Services.AddSingleton<IVoAiTtsClient, VoAiTtsClient>();
builder.Services.AddSingleton<IBlueMagpieChunkCache, BlueMagpieChunkCache>();
builder.Services.AddSingleton<INarrationProvider, EdgeTtsNarrationProvider>();
builder.Services.AddSingleton<IMultiVoiceNarrationProvider, EdgeTtsMultiVoiceNarrationProvider>();
builder.Services.AddSingleton<IMultiVoiceNarrationProvider, ThreeWaVoxCpm2NarrationProvider>();
builder.Services.AddSingleton<FfmpegVoAiAudioComposer>();
builder.Services.AddSingleton<IVoAiAudioComposer>(services =>
    services.GetRequiredService<FfmpegVoAiAudioComposer>());
builder.Services.AddSingleton<IFfmpegAudioComposer>(services =>
    services.GetRequiredService<FfmpegVoAiAudioComposer>());
builder.Services.AddSingleton<IMultiVoiceNarrationProvider, VoAiMultiVoiceNarrationProvider>();
builder.Services.AddSingleton<IMultiVoiceNarrationProvider, BlueMagpieMultiVoiceNarrationProvider>();
builder.Services.AddSingleton<INarrationProviderRegistry, NarrationProviderRegistry>();
builder.Services.AddSingleton<NarrationProviderDispatcher>();
builder.Services.AddHostedService<BlueMagpieChunkCacheCleanupService>();
builder.Services.AddHostedService<StoryPipelineWorker>();

var host = builder.Build();
host.Run();

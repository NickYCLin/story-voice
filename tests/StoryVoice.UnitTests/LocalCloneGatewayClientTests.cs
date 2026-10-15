using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Series;
using StoryVoice.Infrastructure;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.UnitTests;

public sealed class LocalCloneGatewayClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Times_out_when_headers_arrive_but_the_audio_body_stalls(bool partialBody)
    {
        using var cleanup = new CancellationTokenSource();
        using var stream = new StalledAudioStream(partialBody);
        var handler = new StubHandler(_ => CreateStalledResponse(stream));
        using var httpClient = CreateHttpClient(handler);
        httpClient.Timeout = TimeSpan.FromMilliseconds(200);
        var logger = new RecordingLogger<LocalCloneGatewayClient>();
        var client = CreateClient(httpClient, logger);
        var synthesis = client.SynthesizeAsync(
            new LocalCloneGatewayRequest("private-text", "private-transcript", [1]), cleanup.Token);
        try
        {
            var exception = await Assert.ThrowsAsync<LocalClonePreviewUnavailableException>(
                () => synthesis.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Equal(LocalClonePreviewFailureKind.GatewayUnavailable, exception.FailureKind);
            Assert.Contains("FailureClass=Timeout", logger.Combined);
            Assert.DoesNotContain("private-text", logger.Combined);
            Assert.DoesNotContain("private-transcript", logger.Combined);
            Assert.Equal(1, handler.RequestCount);
            Assert.True(stream.WasDisposed);
        }
        finally
        {
            await cleanup.CancelAsync();
            try
            {
                await synthesis;
            }
            catch (Exception exception) when (exception is OperationCanceledException
                or LocalClonePreviewUnavailableException)
            {
            }
        }
    }

    [Fact]
    public async Task Caller_cancellation_during_audio_read_is_not_reported_as_a_gateway_timeout()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var stream = new StalledAudioStream(false);
        var handler = new StubHandler(_ => CreateStalledResponse(stream));
        using var httpClient = CreateHttpClient(handler);
        var logger = new RecordingLogger<LocalCloneGatewayClient>();
        var client = CreateClient(httpClient, logger);
        var synthesis = client.SynthesizeAsync(
            new LocalCloneGatewayRequest("private-text", "private-transcript", [1]), cancellation.Token);

        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => synthesis.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        Assert.Empty(logger.Messages);
        Assert.Equal(1, handler.RequestCount);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task Sends_only_the_pinned_internal_contract_and_accepts_strict_pcm_output()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var output = CreatePcmWave(sampleRate: 24_000, durationSeconds: 1);
        var handler = new StubHandler(_ => CreateSuccessResponse(output));
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.SynthesizeAsync(
            new LocalCloneGatewayRequest(
                "這是私人試音。",
                "這是參考逐字稿。",
                CreatePcmWave(sampleRate: 48_000, durationSeconds: 10)),
            cancellationToken);

        Assert.Equal("audio/wav", result.ContentType);
        Assert.Equal(output, result.Content);
        Assert.Equal(new Uri("http://local-clone-gateway:8082/v1/voice-clone/speech"), handler.RequestUri);
        Assert.Equal(new string('t', 32), handler.Token);
        Assert.Equal(
            ["reference_audio", "reference_text", "text"],
            handler.MultipartNames.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Rejects_an_output_that_is_not_pcm16_24khz_mono()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger<LocalCloneGatewayClient>();
        var handler = new StubHandler(_ => CreateSuccessResponse(
            CreatePcmWave(sampleRate: 48_000, durationSeconds: 1)));
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient, logger);

        var exception = await Assert.ThrowsAsync<LocalClonePreviewUnavailableException>(() =>
            client.SynthesizeAsync(
                new LocalCloneGatewayRequest(
                    "private-preview-text",
                    "private-reference-transcript",
                    [1, 2, 3]),
                cancellationToken));

        Assert.Equal(LocalClonePreviewFailureKind.GatewayContractInvalid, exception.FailureKind);
        Assert.Equal("local_clone_preview_gateway_contract_invalid", exception.StableCode);
        Assert.Contains("FailureClass=ResponseContract", logger.Combined);
        Assert.Contains("ContractStage=Audio", logger.Combined);
        Assert.DoesNotContain("private-preview-text", logger.Combined);
        Assert.DoesNotContain("private-reference-transcript", logger.Combined);
    }

    [Fact]
    public async Task Maps_transient_gateway_status_without_reading_an_error_body()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger<LocalCloneGatewayClient>();
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("private diagnostic must not escape"),
            };
            response.Headers.Add("X-StoryVoice-Local-Clone-Stage", "queue");
            return response;
        });
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient, logger);

        var exception = await Assert.ThrowsAsync<LocalClonePreviewUnavailableException>(() =>
            client.SynthesizeAsync(
                new LocalCloneGatewayRequest(
                    "private-preview-text",
                    "private-reference-transcript",
                    [1, 2, 3]),
                cancellationToken));

        Assert.Equal(LocalClonePreviewFailureKind.GatewayUnavailable, exception.FailureKind);
        Assert.Equal("local_clone_preview_gateway_unavailable", exception.StableCode);
        Assert.Contains("FailureClass=HttpStatus", logger.Combined);
        Assert.Contains("StatusCode=503", logger.Combined);
        Assert.Contains("GatewayStage=queue", logger.Combined);
        Assert.DoesNotContain("private diagnostic must not escape", logger.Combined);
        Assert.DoesNotContain("private-preview-text", logger.Combined);
        Assert.DoesNotContain("private-reference-transcript", logger.Combined);
    }

    [Fact]
    public async Task Replaces_unknown_or_duplicate_gateway_stage_without_logging_it()
    {
        const string privateStage = "private-filename-token-transcript";
        var cancellationToken = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger<LocalCloneGatewayClient>();
        var responses = new Queue<HttpResponseMessage>(
        [
            CreateFailureResponse(privateStage),
            CreateFailureResponse("queue", privateStage),
            CreateFailureResponse(),
        ]);
        var handler = new StubHandler(_ => responses.Dequeue());
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient, logger);

        for (var index = 0; index < 3; index++)
        {
            await Assert.ThrowsAsync<LocalClonePreviewUnavailableException>(() =>
                client.SynthesizeAsync(
                    new LocalCloneGatewayRequest("private-text", "private-transcript", [1]),
                    cancellationToken));
        }

        Assert.Equal(3, logger.Messages.Count);
        Assert.All(logger.Messages, message =>
            Assert.Contains("GatewayStage=unknown_or_front_server", message));
        Assert.DoesNotContain(privateStage, logger.Combined);
        Assert.DoesNotContain("private-text", logger.Combined);
        Assert.DoesNotContain("private-transcript", logger.Combined);
    }

    [Fact]
    public async Task Logs_only_safe_transport_classification()
    {
        const string privateDiagnostic =
            "private-text private-transcript reference.wav internal-token http://private-host";
        var cancellationToken = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger<LocalCloneGatewayClient>();
        using var httpClient = CreateHttpClient(new ThrowingHandler(
            new HttpRequestException(
                HttpRequestError.ConnectionError,
                privateDiagnostic)));
        var client = CreateClient(httpClient, logger);

        var exception = await Assert.ThrowsAsync<LocalClonePreviewUnavailableException>(() =>
            client.SynthesizeAsync(
                new LocalCloneGatewayRequest("private-text", "private-transcript", [1]),
                cancellationToken));

        Assert.Equal(LocalClonePreviewFailureKind.GatewayUnavailable, exception.FailureKind);
        Assert.Null(exception.InnerException);
        Assert.Contains("FailureClass=Transport", logger.Combined);
        Assert.Contains("HttpRequestError=ConnectionError", logger.Combined);
        Assert.DoesNotContain(privateDiagnostic, logger.Combined);
        Assert.DoesNotContain("private-text", logger.Combined);
        Assert.DoesNotContain("private-transcript", logger.Combined);
    }

    [Theory]
    [InlineData(true, "Timeout")]
    [InlineData(false, "Io")]
    public async Task Logs_only_fixed_timeout_or_io_failure_class(
        bool timeout,
        string expectedFailureClass)
    {
        const string privateDiagnostic =
            "private-text private-transcript reference.wav internal-token http://private-host";
        var cancellationToken = TestContext.Current.CancellationToken;
        var logger = new RecordingLogger<LocalCloneGatewayClient>();
        Exception thrown = timeout
            ? new OperationCanceledException(privateDiagnostic)
            : new IOException(privateDiagnostic);
        using var httpClient = CreateHttpClient(new ThrowingHandler(thrown));
        var client = CreateClient(httpClient, logger);

        var exception = await Assert.ThrowsAsync<LocalClonePreviewUnavailableException>(() =>
            client.SynthesizeAsync(
                new LocalCloneGatewayRequest("private-text", "private-transcript", [1]),
                cancellationToken));

        Assert.Equal(LocalClonePreviewFailureKind.GatewayUnavailable, exception.FailureKind);
        Assert.Null(exception.InnerException);
        Assert.Contains($"FailureClass={expectedFailureClass}", logger.Combined);
        Assert.DoesNotContain(privateDiagnostic, logger.Combined);
        Assert.DoesNotContain("private-text", logger.Combined);
        Assert.DoesNotContain("private-transcript", logger.Combined);
    }

    [Fact]
    public async Task Rejects_duplicate_attestation_headers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var output = CreatePcmWave(sampleRate: 24_000, durationSeconds: 1);
        var handler = new StubHandler(_ =>
        {
            var response = CreateSuccessResponse(output);
            response.Headers.TryAddWithoutValidation(
                "X-CosyVoice-Model-Revision",
                LocalClonePreviewOptions.PinnedModelRevision);
            return response;
        });
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<LocalClonePreviewUnavailableException>(() =>
            client.SynthesizeAsync(
                new LocalCloneGatewayRequest("試音", "逐字稿", [1, 2, 3]),
                cancellationToken));

        Assert.Equal(LocalClonePreviewFailureKind.GatewayContractInvalid, exception.FailureKind);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8082/")]
    [InlineData("http://local-clone-gateway:8093/")]
    [InlineData("https://local-clone-gateway:8082/")]
    [InlineData("http://local-clone-gateway:8082/redirect")]
    public void Infrastructure_options_reject_any_non_pinned_gateway_origin(string gatewayBaseUrl)
    {
        using var provider = BuildInfrastructureProvider(new Dictionary<string, string?>
        {
            [$"{LocalClonePreviewOptions.SectionName}:GatewayBaseUrl"] = gatewayBaseUrl,
        });

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<LocalClonePreviewOptions>>().Value);
    }

    [Fact]
    public void Infrastructure_options_reject_parent_traversal_in_private_asset_mapping()
    {
        var profileId = Guid.NewGuid();
        var prefix = $"{LocalClonePreviewOptions.SectionName}:AllowedProfiles:{profileId:D}";
        using var provider = BuildInfrastructureProvider(new Dictionary<string, string?>
        {
            [$"{LocalClonePreviewOptions.SectionName}:Enabled"] = "true",
            [$"{LocalClonePreviewOptions.SectionName}:InternalToken"] = new string('t', 32),
            [$"{LocalClonePreviewOptions.SectionName}:AssetRootPath"] = "private-assets",
            [$"{prefix}:Label"] = "私人試音",
            [$"{prefix}:ReferenceAudioRelativePath"] = "../reference.wav",
            [$"{prefix}:TranscriptRelativePath"] = "transcript.txt",
            [$"{prefix}:ExpectedReferenceAudioSha256"] = new string('a', 64),
            [$"{prefix}:ExpectedTranscriptSha256"] = new string('b', 64),
        });

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<LocalClonePreviewOptions>>().Value);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    public void Infrastructure_options_reject_unsafe_internal_tokens(string token)
    {
        var profileId = Guid.NewGuid();
        var prefix = $"{LocalClonePreviewOptions.SectionName}:AllowedProfiles:{profileId:D}";
        using var provider = BuildInfrastructureProvider(new Dictionary<string, string?>
        {
            [$"{LocalClonePreviewOptions.SectionName}:Enabled"] = "true",
            [$"{LocalClonePreviewOptions.SectionName}:InternalToken"] = token,
            [$"{LocalClonePreviewOptions.SectionName}:AssetRootPath"] = "private-assets",
            [$"{prefix}:Label"] = "私人試音",
            [$"{prefix}:ReferenceAudioRelativePath"] = "reference.wav",
            [$"{prefix}:TranscriptRelativePath"] = "transcript.txt",
            [$"{prefix}:ExpectedReferenceAudioSha256"] = new string('a', 64),
            [$"{prefix}:ExpectedTranscriptSha256"] = new string('b', 64),
        });

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<LocalClonePreviewOptions>>().Value);
    }

    private static ServiceProvider BuildInfrastructureProvider(
        Dictionary<string, string?> localCloneConfiguration)
    {
        var values = new Dictionary<string, string?>(localCloneConfiguration)
        {
            ["ConnectionStrings:Postgres"] =
                "Host=localhost;Database=storyvoice;Username=test;Password=test",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddStoryVoiceInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri(LocalClonePreviewOptions.PinnedGatewayBaseUrl),
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static LocalCloneGatewayClient CreateClient(
        HttpClient httpClient,
        ILogger<LocalCloneGatewayClient>? logger = null) =>
        new(
            httpClient,
            CreateOptions(),
            logger ?? NullLogger<LocalCloneGatewayClient>.Instance);

    private static IOptions<LocalClonePreviewOptions> CreateOptions() => Options.Create(
        new LocalClonePreviewOptions
        {
            Enabled = true,
            InternalToken = new string('t', 32),
            MaximumResponseBytes = 16 * 1024 * 1024,
        });

    private static HttpResponseMessage CreateSuccessResponse(byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        response.Headers.Add(
            "X-CosyVoice-Source-Revision",
            LocalClonePreviewOptions.PinnedCosyVoiceSourceRevision);
        response.Headers.Add("X-CosyVoice-Model-Id", LocalClonePreviewOptions.PinnedModelId);
        response.Headers.Add(
            "X-CosyVoice-Model-Revision",
            LocalClonePreviewOptions.PinnedModelRevision);
        return response;
    }

    private static HttpResponseMessage CreateFailureResponse(params string[] stages)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (stages.Length != 0)
        {
            response.Headers.TryAddWithoutValidation(
                "X-StoryVoice-Local-Clone-Stage",
                stages);
        }

        return response;
    }

    private static byte[] CreatePcmWave(int sampleRate, int durationSeconds)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        const ushort blockAlign = 2;
        var dataLength = checked(sampleRate * durationSeconds * blockAlign);
        var content = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(content);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(4), checked((uint)(content.Length - 8)));
        "WAVEfmt "u8.CopyTo(content.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(22), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(24), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(28), checked((uint)(sampleRate * blockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(34), bitsPerSample);
        "data"u8.CopyTo(content.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(40), checked((uint)dataLength));
        return content;
    }

    private static HttpResponseMessage CreateStalledResponse(Stream stream)
    {
        var response = CreateSuccessResponse(CreatePcmWave(24_000, 1));
        var headers = response.Content.Headers.ToArray();
        response.Content.Dispose();
        response.Content = new StreamContent(stream);
        foreach (var header in headers)
        {
            response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content.Headers.ContentLength = 48_044;
        return response;
    }

    private sealed class StalledAudioStream(bool partialBody) : MemoryStream
    {
        private bool prefixRead;

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            if (partialBody && !prefixRead)
            {
                prefixRead = true;
                "RIFF"u8.CopyTo(buffer.Span);
                return 4;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? Token { get; private set; }

        public string[] MultipartNames { get; private set; } = [];
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            Token = request.Headers.GetValues("X-StoryVoice-Internal-Token").Single();
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            MultipartNames = multipart
                .Select(part => part.Headers.ContentDisposition?.Name?.Trim('"') ?? string.Empty)
                .ToArray();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public string Combined => string.Join(Environment.NewLine, Messages);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, null));
    }
}

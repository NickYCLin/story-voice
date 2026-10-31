using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Series;

namespace StoryVoice.Infrastructure.Narrations;

public sealed class LocalCloneGatewayClient(
    HttpClient httpClient,
    IOptions<LocalClonePreviewOptions> options,
    ILogger<LocalCloneGatewayClient> logger) : ILocalCloneGatewayClient
{
    private const string TokenHeader = "X-StoryVoice-Internal-Token";
    private const string SourceRevisionHeader = "X-CosyVoice-Source-Revision";
    private const string ModelIdHeader = "X-CosyVoice-Model-Id";
    private const string ModelRevisionHeader = "X-CosyVoice-Model-Revision";
    private const string FailureStageHeader = "X-StoryVoice-Local-Clone-Stage";
    private const string UnknownFailureStage = "unknown_or_front_server";
    private static readonly HashSet<string> AllowedFailureStages = new(StringComparer.Ordinal)
    {
        "auth",
        "content_length",
        "multipart",
        "request_contract",
        "upstream_readiness",
        "admission",
        "queue",
        "upstream_terminal",
        "upstream_contract",
    };

    public async Task<LocalCloneGatewayAudio> SynthesizeAsync(
        LocalCloneGatewayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(request.Text), "text");
        multipart.Add(new StringContent(request.ReferenceTranscript), "reference_text");
        var audio = new ByteArrayContent(request.ReferenceAudio);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(audio, "reference_audio", "reference.wav");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/voice-clone/speech")
        {
            Content = multipart,
        };
        httpRequest.Headers.TryAddWithoutValidation(TokenHeader, options.Value.InternalToken);

        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestLifetime.CancelAfter(httpClient.Timeout);
        var requestToken = requestLifetime.Token;

        try
        {
            using var response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Local clone gateway request failed. FailureClass={FailureClass} StatusCode={StatusCode} GatewayStage={GatewayStage}",
                    LocalCloneGatewayFailureClass.HttpStatus,
                    (int)response.StatusCode,
                    ReadSafeFailureStage(response));
                throw FailureFor(response.StatusCode);
            }

            ValidateResponseHeaders(response);
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is null
                || declaredLength <= 0
                || declaredLength > options.Value.MaximumResponseBytes)
            {
                throw ContractInvalid(LocalCloneGatewayContractStage.DeclaredLength);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestToken);
            var content = await ReadBoundedAsync(
                responseStream,
                options.Value.MaximumResponseBytes,
                requestToken);
            if (content.LongLength != declaredLength)
            {
                throw ContractInvalid(LocalCloneGatewayContractStage.BodyLength);
            }

            try
            {
                LocalClonePcmWaveValidator.ValidateOutput(content, options.Value.MaximumResponseBytes);
            }
            catch (InvalidDataException)
            {
                throw ContractInvalid(LocalCloneGatewayContractStage.Audio);
            }

            return new LocalCloneGatewayAudio(content, "audio/wav");
        }
        catch (LocalClonePreviewUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Local clone gateway request failed. FailureClass={FailureClass}",
                LocalCloneGatewayFailureClass.Timeout);
            throw new LocalClonePreviewUnavailableException(
                LocalClonePreviewFailureKind.GatewayUnavailable);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                "Local clone gateway request failed. FailureClass={FailureClass} HttpRequestError={HttpRequestError}",
                LocalCloneGatewayFailureClass.Transport,
                exception.HttpRequestError);
            throw new LocalClonePreviewUnavailableException(
                LocalClonePreviewFailureKind.GatewayUnavailable);
        }
        catch (IOException)
        {
            logger.LogWarning(
                "Local clone gateway request failed. FailureClass={FailureClass}",
                LocalCloneGatewayFailureClass.Io);
            throw new LocalClonePreviewUnavailableException(
                LocalClonePreviewFailureKind.GatewayUnavailable);
        }
    }

    private void ValidateResponseHeaders(HttpResponseMessage response)
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "audio/wav",
                StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentEncoding.Count != 0
            || response.Headers.CacheControl?.NoStore != true
            || !HasSingleExactHeader(
                response,
                SourceRevisionHeader,
                LocalClonePreviewOptions.PinnedCosyVoiceSourceRevision)
            || !HasSingleExactHeader(
                response,
                ModelIdHeader,
                LocalClonePreviewOptions.PinnedModelId)
            || !HasSingleExactHeader(
                response,
                ModelRevisionHeader,
                LocalClonePreviewOptions.PinnedModelRevision))
        {
            throw ContractInvalid(LocalCloneGatewayContractStage.Headers);
        }
    }

    private static bool HasSingleExactHeader(
        HttpResponseMessage response,
        string name,
        string expected)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return false;
        }

        var candidates = values.Take(2).ToArray();
        return candidates.Length == 1
            && string.Equals(candidates[0], expected, StringComparison.Ordinal);
    }

    private static string ReadSafeFailureStage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(FailureStageHeader, out var values))
        {
            return UnknownFailureStage;
        }

        var candidates = values.Take(2).ToArray();
        return candidates.Length == 1 && AllowedFailureStages.Contains(candidates[0])
            ? candidates[0]
            : UnknownFailureStage;
    }

    private async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var output = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length > maximumBytes - read)
                {
                    throw ContractInvalid(LocalCloneGatewayContractStage.BodyLength);
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static LocalClonePreviewUnavailableException FailureFor(HttpStatusCode statusCode) =>
        new((int)statusCode is 408 or 425 or 429 or >= 500
            ? LocalClonePreviewFailureKind.GatewayUnavailable
            : LocalClonePreviewFailureKind.GatewayContractInvalid);

    private LocalClonePreviewUnavailableException ContractInvalid(
        LocalCloneGatewayContractStage stage)
    {
        logger.LogWarning(
            "Local clone gateway response contract was rejected. FailureClass={FailureClass} ContractStage={ContractStage}",
            LocalCloneGatewayFailureClass.ResponseContract,
            stage);
        return new LocalClonePreviewUnavailableException(
            LocalClonePreviewFailureKind.GatewayContractInvalid);
    }

    private enum LocalCloneGatewayFailureClass
    {
        HttpStatus,
        Timeout,
        Transport,
        Io,
        ResponseContract,
    }

    private enum LocalCloneGatewayContractStage
    {
        Headers,
        DeclaredLength,
        BodyLength,
        Audio,
    }
}

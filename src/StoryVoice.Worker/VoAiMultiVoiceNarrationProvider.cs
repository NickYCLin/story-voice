using System.Globalization;
using Microsoft.Extensions.Options;
using StoryVoice.Domain.Narrations;

namespace StoryVoice.Worker;

public sealed class VoAiMultiVoiceNarrationProvider(
    IVoAiTtsClient client,
    IVoAiAudioComposer composer,
    IOptions<VoAiOptions> options,
    ILogger<VoAiMultiVoiceNarrationProvider> logger) : IMultiVoiceNarrationProvider
{
    private const int MaximumTextLength = 1_000;
    private const string VoiceReferenceVersion = "v1";
    private const string BreakCharacters = "\n\r。！？；，、.!?;,";

    public string ProviderName => CharacterVoiceProviders.VoAi;

    public async Task<MultiVoiceSynthesisResult> SynthesizeAsync(
        MultiVoiceNarrationRequest request,
        string outputPath,
        Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            var settings = options.Value;
            if (settings.MaximumConcurrentChunks is < 1 or > 4)
            {
                throw new InvalidOperationException("VoAI chunk concurrency must be between 1 and 4.");
            }
            if (request.Turns.Count == 0)
            {
                throw new ArgumentException("At least one VoAI narration turn is required.", nameof(request));
            }

            var preparedTurns = request.Turns.Select(PrepareTurn).ToArray();
            var totalChunks = preparedTurns.Sum(turn => turn.Chunks.Count);
            if (totalChunks == 0)
            {
                throw new ArgumentException("VoAI narration contains no synthesizable text.", nameof(request));
            }

            if (totalChunks > settings.MaximumChunksPerJob)
            {
                throw new InvalidOperationException("VoAI narration exceeds the configured chunk budget.");
            }

            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            Directory.CreateDirectory(outputDirectory);
            var workDirectory = Path.Combine(outputDirectory, $"voai-tts-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDirectory);
            try
            {
                var chunks = preparedTurns.SelectMany((turn, turnIndex) =>
                    turn.Chunks.Select((_, chunkIndex) => (Turn: turn, TurnIndex: turnIndex, ChunkIndex: chunkIndex)))
                    .ToArray();
                var audioSegments = new VoAiAudioSegment[totalChunks];
                using var progressGate = new SemaphoreSlim(1, 1);
                var completedChunks = 0;
                long receivedAudioBytes = 0;
                // ForEachAsync bounds the running work, cancels siblings on failure and drains them
                // before returning. The work directory must outlive every open response/file.
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, chunks.Length),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = settings.MaximumConcurrentChunks,
                        CancellationToken = cancellationToken,
                    },
                    async (index, chunkCancellation) =>
                    {
                        var (turn, turnIndex, chunkIndex) = chunks[index];
                        chunkCancellation.ThrowIfCancellationRequested();
                        var wavPath = Path.Combine(workDirectory, $"{turnIndex:00000}-{chunkIndex:00000}.wav");
                        await using (var destination = new FileStream(
                            wavPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 81_920,
                            FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            await client.SynthesizeWavAsync(
                                new VoAiSpeechSynthesisRequest(
                                    turn.Chunks[chunkIndex],
                                    turn.Voice.Model,
                                    turn.Voice.Speaker,
                                    turn.Voice.Style,
                                    turn.Speed,
                                    turn.PitchShift),
                                destination,
                                chunkCancellation);
                        }

                        if (!File.Exists(wavPath) || new FileInfo(wavPath).Length < 1)
                        {
                            throw new InvalidOperationException("VoAI returned an empty WAV segment.");
                        }

                        await progressGate.WaitAsync(chunkCancellation);
                        try
                        {
                            receivedAudioBytes = checked(receivedAudioBytes + new FileInfo(wavPath).Length);
                            if (receivedAudioBytes > settings.MaximumJobResponseBytes)
                            {
                                throw new InvalidOperationException(
                                    "VoAI narration exceeds the configured aggregate audio budget.");
                            }

                            audioSegments[index] = new VoAiAudioSegment(
                                wavPath,
                                turn.Volume,
                                chunkIndex == 0 ? turn.PauseBeforeMs : 0,
                                TurnIndex: turnIndex);
                            completedChunks++;
                            if (progressCallback is not null)
                            {
                                await progressCallback(
                                    new NarrationSynthesisProgress(completedChunks, totalChunks),
                                    chunkCancellation);
                            }
                        }
                        finally
                        {
                            progressGate.Release();
                        }
                    });

                var result = await composer.ComposeAsync(audioSegments, outputPath, cancellationToken);
                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1)
                {
                    throw new InvalidOperationException("VoAI audio composition produced no MP3 output.");
                }
                return result;
            }
            finally
            {
                TryDeleteDirectory(workDirectory);
            }

        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PermanentNarrationProviderException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PermanentNarrationProviderException(
                "voai_provider_failed",
                "VoAI synthesis failed and was not automatically retried.",
                exception);
        }
    }

    internal static VoAiVoiceReference ParseVoiceReference(string voiceReference)
    {
        if (string.IsNullOrWhiteSpace(voiceReference))
        {
            throw new ArgumentException("VoAI voice reference is required.", nameof(voiceReference));
        }

        var parts = voiceReference.Split(':', StringSplitOptions.None);
        if (parts.Length != 4
            || !string.Equals(parts[0], VoiceReferenceVersion, StringComparison.Ordinal)
            || parts.Skip(1).Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "VoAI voice reference must use the v1:model:speaker:style format.",
                nameof(voiceReference));
        }

        return new VoAiVoiceReference(parts[1], parts[2], parts[3]);
    }

    internal static double MapRateToSpeed(string rate)
    {
        var percent = ParseSignedAdjustment(rate, "%", nameof(rate));
        return Math.Clamp(1 + (percent / 100), 0.5, 1.5);
    }

    internal static int MapPitchToShift(string pitch)
    {
        var hertz = ParseSignedAdjustment(pitch, "Hz", nameof(pitch));
        var shifted = Math.Round(hertz / 5, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(shifted, -5, 5);
    }

    internal static IReadOnlyList<string> SplitText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var remaining = text.Trim();
        var chunks = new List<string>();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= MaximumTextLength)
            {
                chunks.Add(remaining);
                break;
            }

            var window = remaining[..MaximumTextLength];
            var boundary = -1;
            foreach (var breakCharacter in BreakCharacters)
            {
                var index = window.LastIndexOf(breakCharacter);
                if (index > boundary)
                {
                    boundary = index;
                }
            }

            var cut = boundary >= MaximumTextLength / 2 ? boundary + 1 : MaximumTextLength;
            // Keep supplementary characters intact without increasing the UTF-16 request limit.
            if (char.IsSurrogatePair(remaining, cut - 1)) cut--;
            var chunk = remaining[..cut].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            remaining = remaining[cut..].TrimStart();
        }

        return chunks;
    }

    private static PreparedTurn PrepareTurn(NarrationTurn turn)
    {
        if (string.IsNullOrWhiteSpace(turn.Text))
        {
            throw new ArgumentException("VoAI narration turn text is required.", nameof(turn));
        }

        if (turn.PauseBeforeMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(turn), "VoAI pause must not be negative.");
        }

        _ = ParseSignedAdjustment(turn.Volume, "%", nameof(turn.Volume));

        return new PreparedTurn(
            ParseVoiceReference(turn.Voice),
            MapRateToSpeed(turn.Rate),
            MapPitchToShift(turn.Pitch),
            turn.Volume,
            turn.PauseBeforeMs,
            SplitText(turn.Text));
    }

    private static double ParseSignedAdjustment(string value, string unit, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"VoAI {parameterName} is required.", parameterName);
        }

        var numericValue = value.Trim();
        if (!numericValue.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"VoAI {parameterName} must be a signed {unit} adjustment.",
                parameterName);
        }

        numericValue = numericValue[..^unit.Length];

        if (!double.TryParse(
                numericValue,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed)
            || !double.IsFinite(parsed))
        {
            throw new ArgumentException(
                $"VoAI {parameterName} must be a signed {unit} adjustment.",
                parameterName);
        }

        return parsed;
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Unable to remove a VoAI synthesis working directory");
        }
    }

    internal sealed record VoAiVoiceReference(string Model, string Speaker, string Style);

    private sealed record PreparedTurn(
        VoAiVoiceReference Voice,
        double Speed,
        int PitchShift,
        string Volume,
        int PauseBeforeMs,
        IReadOnlyList<string> Chunks);
}

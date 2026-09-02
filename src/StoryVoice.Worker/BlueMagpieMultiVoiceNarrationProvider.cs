using System.Text;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Worker;

/// <summary>
/// Version-pinned, local-only multi-character narration through the BlueMagpie gateway. The
/// gateway accepts at most 200 Unicode scalar values per request; the production provider uses a
/// more conservative 120-scalar canary limit, so a complete turn is safely
/// chunked and its WAV responses are composed locally. BlueMagpie does not expose rate or pitch
/// controls: only the canonical neutral values are accepted instead of silently changing the
/// confirmed cast semantics. Volume and pause are applied by the shared ffmpeg composer.
/// </summary>
public sealed class BlueMagpieMultiVoiceNarrationProvider(
    IBlueMagpieTtsClient client,
    IBlueMagpieChunkCache chunkCache,
    IFfmpegAudioComposer composer,
    IOptions<BlueMagpieOptions> options,
    ILogger<BlueMagpieMultiVoiceNarrationProvider> logger) : IVersionedMultiVoiceNarrationProvider
{
    public const string PinnedProviderVersion = BlueMagpieOptions.PinnedProviderVersion;
    internal const int MaximumTextScalarsPerChunk = BlueMagpieOptions.MaximumTextScalarsPerChunk;
    private const int OutputSampleRate = 48_000;
    private const int MaximumPauseBeforeMs = 60_000;
    private const string NeutralRate = "+0%";
    private const string NeutralPitch = "+0Hz";
    private const string BreakCharacters = "\n\r。！？；，、.!?;,";

    public string ProviderName => CharacterVoiceProviders.BlueMagpie;

    public string ProviderVersion => PinnedProviderVersion;

    public async Task<MultiVoiceSynthesisResult> SynthesizeAsync(
        MultiVoiceNarrationRequest request,
        string outputPath,
        Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        string? ownedOutputPath = null;
        var completed = false;
        var cacheHits = 0;
        var cacheMisses = 0;
        Guid? cacheJobId = null;
        try
        {
            var configured = ValidateRuntime();
            ArgumentNullException.ThrowIfNull(request);
            NarrationProviderContractValidator.Validate(this, ProviderName, request);
            var cacheContext = ValidateCacheContext(request.CacheContext);
            cacheJobId = cacheContext.JobId;
            var preparedTurns = PrepareTurns(request);
            var estimatedChunkCount = preparedTurns.Sum(turn => (long)turn.Chunks.Count);
            if (estimatedChunkCount is < 1 || estimatedChunkCount > configured.MaximumChunksPerJob)
            {
                throw PermanentFailure("BlueMagpie narration exceeds the safe chunk budget.");
            }

            var totalChunks = checked((int)estimatedChunkCount);

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw PermanentFailure("BlueMagpie output path is required.");
            }

            var fullOutputPath = Path.GetFullPath(outputPath);
            if (File.Exists(fullOutputPath))
            {
                throw PermanentFailure("BlueMagpie output path must not already exist.");
            }

            ownedOutputPath = fullOutputPath;
            var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
            Directory.CreateDirectory(outputDirectory);

            var audioSegments = new List<FfmpegAudioSegment>(totalChunks);
            var completedChunks = 0;
            long resolvedAudioBytes = 0;
            await using var cacheScope = await chunkCache.OpenScopeAsync(
                cacheContext,
                cancellationToken);
            for (var turnIndex = 0; turnIndex < preparedTurns.Count; turnIndex++)
            {
                var turn = preparedTurns[turnIndex];
                for (var chunkIndex = 0; chunkIndex < turn.Chunks.Count; chunkIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var effectivePauseBeforeMs = chunkIndex == 0 ? turn.PauseBeforeMs : 0;
                    var text = turn.Chunks[chunkIndex];
                    var entry = await cacheScope.GetOrCreateAsync(
                        new BlueMagpieChunkCacheRequest(
                            completedChunks,
                            text,
                            turn.Voice,
                            NeutralRate,
                            NeutralPitch,
                            turn.Volume,
                            effectivePauseBeforeMs,
                            ProviderVersion,
                            BlueMagpieOptions.PinnedModelRevision),
                        async synthesisCancellationToken =>
                        {
                            var result = await client.SynthesizeAsync(
                                text,
                                turn.Voice,
                                synthesisCancellationToken);
                            ValidateSynthesisResult(result, turn.Voice);
                            return result.Content;
                        },
                        cancellationToken);
                    if (entry.CacheHit)
                    {
                        cacheHits++;
                    }
                    else
                    {
                        cacheMisses++;
                    }

                    resolvedAudioBytes = checked(resolvedAudioBytes + entry.AudioBytes);
                    if (resolvedAudioBytes > configured.MaximumJobAudioBytes)
                    {
                        throw PermanentFailure(
                            "BlueMagpie narration exceeds the aggregate audio budget.");
                    }

                    audioSegments.Add(new FfmpegAudioSegment(
                        entry.InputWavPath,
                        turn.Volume,
                        effectivePauseBeforeMs,
                        DeleteInputAfterNormalization: false));
                    completedChunks++;
                    if (progressCallback is not null)
                    {
                        await progressCallback(
                            new NarrationSynthesisProgress(completedChunks, totalChunks),
                            cancellationToken);
                    }
                }
            }

            await composer.ComposeAsync(
                audioSegments,
                fullOutputPath,
                OutputSampleRate,
                cancellationToken);
            if (!File.Exists(fullOutputPath) || new FileInfo(fullOutputPath).Length < 1)
            {
                throw new InvalidOperationException("BlueMagpie audio composition produced no MP3 output.");
            }

            completed = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PermanentNarrationProviderException)
        {
            throw;
        }
        catch (BlueMagpieChunkCacheCapacityException exception)
        {
            throw new InvalidOperationException("bluemagpie_cache_capacity_exhausted", exception);
        }
        catch (SeriesVoicePreviewUnavailableException exception)
            when (exception.FailureKind == SeriesVoicePreviewFailureKind.ContractViolation)
        {
            throw PermanentFailure("BlueMagpie gateway violated the pinned audio contract.", exception);
        }
        catch (SeriesVoicePreviewUnavailableException exception)
        {
            // The current client deliberately collapses gateway 503, Redis contention, connect
            // failures and timeouts to this availability exception. These are retryable and must
            // never be upgraded to a permanent job failure.
            throw new InvalidOperationException("bluemagpie_provider_unavailable", exception);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "bluemagpie_provider_failed",
                exception);
        }
        finally
        {
            if (!completed)
            {
                TryDeleteFile(ownedOutputPath);
            }

            if (cacheJobId is not null && cacheHits + cacheMisses > 0)
            {
                logger.LogInformation(
                    "BlueMagpie cache activity for job {JobId}: {CacheHits} hits and {CacheMisses} misses",
                    cacheJobId,
                    cacheHits,
                    cacheMisses);
            }
        }

        return MultiVoiceSynthesisResult.None;
    }

    internal static IReadOnlyList<string> SplitText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var runes = text.EnumerateRunes().ToArray();
        var chunks = new List<string>();
        var start = 0;
        while (start < runes.Length)
        {
            var limit = Math.Min(start + MaximumTextScalarsPerChunk, runes.Length);
            var end = limit;
            if (limit < runes.Length)
            {
                var minimumBoundary = start + BlueMagpieOptions.PreferredBreakSearchFloorScalars;
                for (var index = limit - 1; index >= minimumBoundary; index--)
                {
                    if (IsBreak(runes[index]))
                    {
                        end = index + 1;
                        break;
                    }
                }
            }

            var chunk = string.Concat(runes[start..end].Select(rune => rune.ToString())).Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            start = end;
        }

        return chunks;
    }

    private static IReadOnlyList<PreparedTurn> PrepareTurns(MultiVoiceNarrationRequest request)
    {
        if (request.Turns is null || request.Turns.Count == 0)
        {
            throw PermanentFailure("BlueMagpie narration requires at least one turn.");
        }

        var prepared = new List<PreparedTurn>(request.Turns.Count);
        foreach (var turn in request.Turns)
        {
            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                throw PermanentFailure("BlueMagpie narration turn text is required.");
            }

            if (!string.Equals(turn.Voice, BlueMagpieOptions.MaleVoice, StringComparison.Ordinal)
                && !string.Equals(turn.Voice, BlueMagpieOptions.FemaleVoice, StringComparison.Ordinal))
            {
                throw PermanentFailure("BlueMagpie voice is not in the pinned local voice set.");
            }

            if (!string.Equals(turn.Rate, NeutralRate, StringComparison.Ordinal)
                || !string.Equals(turn.Pitch, NeutralPitch, StringComparison.Ordinal))
            {
                throw PermanentFailure(
                    "BlueMagpie requires the canonical neutral rate and pitch values.");
            }

            try
            {
                _ = FfmpegVoAiAudioComposer.ParseVolumeFactor(turn.Volume);
            }
            catch (ArgumentException exception)
            {
                throw PermanentFailure("BlueMagpie volume is invalid.", exception);
            }

            if (turn.PauseBeforeMs is < 0 or > MaximumPauseBeforeMs)
            {
                throw PermanentFailure("BlueMagpie pause must be between 0 and 60000 milliseconds.");
            }

            var chunks = SplitText(turn.Text);
            if (chunks.Count == 0)
            {
                throw PermanentFailure("BlueMagpie narration contains no synthesizable text.");
            }

            prepared.Add(new PreparedTurn(turn.Voice, turn.Volume, turn.PauseBeforeMs, chunks));
        }

        return prepared;
    }

    private BlueMagpieOptions ValidateRuntime()
    {
        var configured = options.Value;
        if (!configured.Enabled
            || !configured.FormalNarrationEnabled
            || !string.Equals(
                configured.ModelRevision,
                BlueMagpieOptions.PinnedModelRevision,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(configured.InternalToken)
            || configured.InternalToken.Length < 32)
        {
            throw new PermanentNarrationProviderException(
                "provider_version_mismatch",
                "BlueMagpie formal narration is disabled or its pinned runtime configuration does not match.");
        }

        return configured;
    }

    private static bool IsBreak(Rune rune) =>
        BreakCharacters.Contains(rune.ToString(), StringComparison.Ordinal);

    private static PermanentNarrationProviderException PermanentFailure(
        string message,
        Exception? innerException = null) =>
        new("bluemagpie_provider_contract_invalid", message, innerException);

    private static NarrationSynthesisCacheContext ValidateCacheContext(
        NarrationSynthesisCacheContext? context)
    {
        if (context is null
            || context.OwnerId == Guid.Empty
            || context.JobId == Guid.Empty
            || context.CastRevisionId == Guid.Empty
            || string.IsNullOrWhiteSpace(context.SourceHash)
            || string.IsNullOrWhiteSpace(context.CastFingerprint)
            || string.IsNullOrWhiteSpace(context.SpeechPlanFingerprint)
            || string.IsNullOrWhiteSpace(context.CompositionVersion)
            || string.IsNullOrWhiteSpace(context.FfmpegProfile))
        {
            throw PermanentFailure("BlueMagpie formal narration requires an immutable cache context.");
        }

        return context;
    }

    private static void ValidateSynthesisResult(
        BlueMagpieSynthesisResult result,
        string expectedVoice)
    {
        if (!string.Equals(
                result.ModelRevision,
                BlueMagpieOptions.PinnedModelRevision,
                StringComparison.Ordinal)
            || !string.Equals(result.ProviderVersion, PinnedProviderVersion, StringComparison.Ordinal)
            || !string.Equals(result.Voice, expectedVoice, StringComparison.Ordinal)
            || !string.Equals(result.ContentType, "audio/wav", StringComparison.Ordinal)
            || result.Content.Length < 1)
        {
            throw PermanentFailure("BlueMagpie returned an invalid pinned WAV response.");
        }
    }

    private void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Unable to remove an incomplete BlueMagpie output artifact");
        }
    }

    private sealed record PreparedTurn(
        string Voice,
        string Volume,
        int PauseBeforeMs,
        IReadOnlyList<string> Chunks);
}

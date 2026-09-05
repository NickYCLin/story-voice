using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Series;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Worker;

/// <summary>
/// Multi-character synthesis through the privately self-hosted CosyVoice3 clone gateway. Every
/// <c>localclone:</c> turn is voiced from the character's own stored reference recording; turns
/// that would otherwise fall back to a catalog narrator voice are voiced by the point-of-view
/// clone instead, so no third-party TTS is ever used. Each synthesized chunk is cached on disk
/// under the job so a retried attempt resumes instead of paying for the whole book again.
/// </summary>
public sealed class LocalCloneMultiVoiceNarrationProvider(
    ILocalCloneGatewayClient client,
    IOptions<LocalClonePreviewOptions> localCloneOptions,
    IOptions<NarrationOptions> narrationOptions,
    ILogger<LocalCloneMultiVoiceNarrationProvider> logger) : IMultiVoiceNarrationProvider
{
    public const string UnavailableCode = "local_clone_formal_narration_disabled";
    public const string MissingReferenceCode = "local_clone_reference_missing";

    // CosyVoice3 quality drops on very long single utterances and the gateway caps output at
    // 300 s, so keep chunks to roughly one paragraph.
    private const int MaxChars = 160;
    private const int MaxGatewayAttempts = 6;
    private static readonly TimeSpan GatewayRetryDelay = TimeSpan.FromSeconds(20);
    private const string BreakCharacters = "\n。！？；，、,.!?:：";

    public string ProviderName => CharacterVoiceProviders.LocalClone;

    public async Task SynthesizeAsync(
        MultiVoiceNarrationRequest request,
        string outputPath,
        Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!localCloneOptions.Value.FormalNarrationEnabled)
        {
            throw new PermanentNarrationProviderException(
                UnavailableCode,
                "自架克隆聲線的正式配音尚未由管理員啟用（LocalClonePreview__FormalNarrationEnabled）。");
        }

        if (request.Turns.Count == 0)
        {
            throw new ArgumentException("多角色語音 manifest 至少要有一個 turn。", nameof(request));
        }

        var references = request.CloneReferences ?? new Dictionary<string, LocalCloneVoiceReference>();
        var fallbackReferenceKey = ResolveFallbackReferenceKey(request.Turns, references);
        var resolvedTurns = request.Turns
            .Select(turn => (Turn: turn, ReferenceKey: ResolveReferenceKey(turn, references, fallbackReferenceKey)))
            .ToArray();

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(outputDirectory);
        var workDirectory = Path.Combine(outputDirectory, $"local-clone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var cacheDirectory = ResolveCacheDirectory(request);
        Directory.CreateDirectory(cacheDirectory);
        var referenceAudioCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            var turnChunks = resolvedTurns
                .Select(entry => (entry.Turn, entry.ReferenceKey, Chunks: SplitText(entry.Turn.Text, MaxChars)))
                .ToArray();
            var totalChunks = turnChunks.Sum(entry => entry.Chunks.Count);
            if (totalChunks == 0)
            {
                throw new InvalidOperationException("自架克隆聲線合成沒有可用文字。");
            }

            var sequence = new List<string>();
            var completed = 0;
            for (var turnIndex = 0; turnIndex < turnChunks.Length; turnIndex++)
            {
                var (turn, referenceKey, chunks) = turnChunks[turnIndex];
                if (turn.PauseBeforeMs > 0)
                {
                    var silencePath = Path.Combine(workDirectory, $"{turnIndex:0000}-pause.mp3");
                    await GenerateSilenceAsync(silencePath, turn.PauseBeforeMs, cancellationToken);
                    sequence.Add(silencePath);
                }

                var reference = references[referenceKey];
                for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cachedPath = Path.Combine(cacheDirectory, $"{ChunkCacheKey(referenceKey, chunks[chunkIndex])}.mp3");
                    if (!File.Exists(cachedPath) || new FileInfo(cachedPath).Length < 1)
                    {
                        await SynthesizeChunkAsync(
                            reference,
                            referenceAudioCache,
                            chunks[chunkIndex],
                            cachedPath,
                            workDirectory,
                            cancellationToken);
                    }

                    sequence.Add(cachedPath);
                    completed++;
                    if (progressCallback is not null)
                    {
                        await progressCallback(new NarrationSynthesisProgress(completed, totalChunks), cancellationToken);
                    }
                }
            }

            var candidate = Path.Combine(workDirectory, "complete.mp3");
            await ConcatAsync(sequence, candidate, cancellationToken);
            var duration = await ProbeDurationSecondsAsync(candidate, cancellationToken);
            if (duration <= 0 || !File.Exists(candidate) || new FileInfo(candidate).Length < 1)
            {
                throw new InvalidOperationException("自架克隆聲線合成沒有產生可用音訊。");
            }

            File.Copy(candidate, outputPath, overwrite: true);
            TryDeleteDirectory(cacheDirectory, logger);
        }
        finally
        {
            TryDeleteDirectory(workDirectory, logger);
        }
    }

    private static string ResolveReferenceKey(
        NarrationTurn turn,
        IReadOnlyDictionary<string, LocalCloneVoiceReference> references,
        string? fallbackReferenceKey)
    {
        if (turn.Voice.StartsWith(MultiCharacterTurnBuilder.LocalCloneReferencePrefix, StringComparison.Ordinal))
        {
            if (!references.ContainsKey(turn.Voice))
            {
                throw new PermanentNarrationProviderException(
                    MissingReferenceCode,
                    "自架克隆聲線缺少角色的參考錄音或逐字稿；請確認角色庫聲線已 Ready。");
            }

            return turn.Voice;
        }

        if (turn.Voice.StartsWith("design:", StringComparison.Ordinal))
        {
            throw new PermanentNarrationProviderException(
                ThreeWaSynthesisCapabilities.DesignVoiceUnavailableCode,
                ThreeWaSynthesisCapabilities.DesignVoiceUnavailableMessage);
        }

        // Narrator / narrator-fallback turns carry a catalog voice id. This provider never calls a
        // third-party TTS, so they are voiced by the point-of-view clone instead.
        return fallbackReferenceKey
            ?? throw new PermanentNarrationProviderException(
                MissingReferenceCode,
                "自架克隆聲線系列沒有任何可用的角色克隆聲線可作為旁白。");
    }

    /// <summary>The clone used for narrator-fallback turns: the reference that voices the most
    /// text in this job, which under point-of-view narration is the POV character.</summary>
    internal static string? ResolveFallbackReferenceKey(
        IReadOnlyList<NarrationTurn> turns,
        IReadOnlyDictionary<string, LocalCloneVoiceReference> references)
    {
        return turns
            .Where(turn => turn.Voice.StartsWith(MultiCharacterTurnBuilder.LocalCloneReferencePrefix, StringComparison.Ordinal)
                && references.ContainsKey(turn.Voice))
            .GroupBy(turn => turn.Voice, StringComparer.Ordinal)
            .OrderByDescending(group => group.Sum(turn => turn.Text.Length))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();
    }

    private string ResolveCacheDirectory(MultiVoiceNarrationRequest request)
    {
        var scope = request.CacheContext is { } context
            ? $"{context.JobId:N}-{context.CastRevisionId:N}"
            : $"adhoc-{Guid.NewGuid():N}";
        return Path.Combine(narrationOptions.Value.AudioRootPath, "local-clone-cache", scope);
    }

    private static string ChunkCacheKey(string referenceKey, string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"{referenceKey}\n{text}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private async Task SynthesizeChunkAsync(
        LocalCloneVoiceReference reference,
        Dictionary<string, byte[]> referenceAudioCache,
        string text,
        string cachedPath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        if (!referenceAudioCache.TryGetValue(reference.ReferenceAudioPath, out var audio))
        {
            if (!File.Exists(reference.ReferenceAudioPath))
            {
                throw new PermanentNarrationProviderException(
                    MissingReferenceCode,
                    "自架克隆聲線的參考錄音檔案不存在。");
            }

            audio = await File.ReadAllBytesAsync(reference.ReferenceAudioPath, cancellationToken);
            referenceAudioCache[reference.ReferenceAudioPath] = audio;
        }

        LocalCloneGatewayAudio? result = null;
        for (var attempt = 1; attempt <= MaxGatewayAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result = await client.SynthesizeAsync(
                    new LocalCloneGatewayRequest(text, reference.ReferenceTranscript, audio),
                    cancellationToken);
                break;
            }
            catch (LocalClonePreviewUnavailableException exception) when (attempt < MaxGatewayAttempts)
            {
                // The gateway is single-flight and the executor may be warming the model or
                // waiting on the shared GPU lease; back off instead of failing the whole book.
                logger.LogWarning(
                    exception,
                    "Local clone gateway unavailable (attempt {Attempt}/{MaxAttempts}); retrying",
                    attempt,
                    MaxGatewayAttempts);
                await Task.Delay(GatewayRetryDelay, cancellationToken);
            }
        }

        if (result is null)
        {
            throw new InvalidOperationException("自架克隆聲線 gateway 持續無法合成。");
        }

        var rawPath = Path.Combine(workDirectory, $"{Path.GetFileNameWithoutExtension(cachedPath)}-raw.wav");
        await File.WriteAllBytesAsync(rawPath, result.Content, cancellationToken);
        var temporaryPath = cachedPath + ".tmp.mp3";
        await TranscodeAsync(rawPath, temporaryPath, cancellationToken);
        File.Delete(rawPath);
        if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length < 1)
        {
            throw new InvalidOperationException("自架克隆聲線音檔轉檔失敗。");
        }

        File.Move(temporaryPath, cachedPath, overwrite: true);
    }

    internal static IReadOnlyList<string> SplitText(string text, int maxChars)
    {
        var remaining = text.Trim();
        var chunks = new List<string>();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxChars)
            {
                chunks.Add(remaining);
                break;
            }

            var window = remaining[..maxChars];
            var boundary = -1;
            foreach (var breakCharacter in BreakCharacters)
            {
                var index = window.LastIndexOf(breakCharacter);
                if (index > boundary)
                {
                    boundary = index;
                }
            }

            var cut = boundary >= maxChars / 2 ? boundary + 1 : maxChars;
            var chunk = remaining[..cut].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            remaining = remaining[cut..].TrimStart();
        }

        return chunks;
    }

    // 固定 44100 Hz／mono MP3：串接時是 -c copy，所有片段（含靜音）的 codec 參數必須一致。
    private static async Task TranscodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken) =>
        await RunFfmpegAsync(
            ["-y", "-i", inputPath, "-ar", "44100", "-ac", "1", "-c:a", "libmp3lame", "-q:a", "2", outputPath],
            cancellationToken);

    private static async Task GenerateSilenceAsync(string outputPath, int milliseconds, CancellationToken cancellationToken)
    {
        var seconds = Math.Max(milliseconds, 0) / 1000.0;
        await RunFfmpegAsync(
            [
                "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono",
                "-t", seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-c:a", "libmp3lame", "-q:a", "9", outputPath
            ],
            cancellationToken);
    }

    private static async Task ConcatAsync(IReadOnlyList<string> parts, string outputPath, CancellationToken cancellationToken)
    {
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("沒有可合併的音訊片段。");
        }

        var listPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "concat_list.txt");
        var lines = parts.Select(part => $"file '{part.Replace("'", "'\\''")}'");
        await File.WriteAllLinesAsync(listPath, lines, cancellationToken);
        await RunFfmpegAsync(
            ["-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", outputPath],
            cancellationToken);
    }

    private static async Task<double> ProbeDurationSecondsAsync(string path, CancellationToken cancellationToken)
    {
        var output = await RunProcessAsync(
            "ffprobe",
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path],
            cancellationToken);
        return double.TryParse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var duration)
            ? duration
            : 0;
    }

    private static Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunProcessAsync("ffmpeg", arguments, cancellationToken);

    private static async Task<string> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"無法啟動 {fileName}。");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} 執行失敗（結束碼 {process.ExitCode}）：{stderr}");
        }

        return stdout;
    }

    private static void TryDeleteDirectory(string path, ILogger logger)
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
            logger.LogWarning(exception, "Failed to delete local clone work directory {Path}", path);
        }
    }
}

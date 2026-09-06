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
                    if (!HasSpeakableText(chunks[chunkIndex]))
                    {
                        // CosyVoice's Chinese front end drops text without any CJK characters
                        // (chapter headers such as "Atlantis AM8：20") and then reports zero
                        // output, which the gateway surfaces as a 503. Nothing to voice here.
                        logger.LogInformation(
                            "Local clone skipped a chunk without speakable CJK text ({Length} chars)",
                            chunks[chunkIndex].Length);
                        completed++;
                        continue;
                    }

                    var cachedPath = Path.Combine(cacheDirectory, $"{ChunkCacheKey(referenceKey, chunks[chunkIndex])}.mp3");
                    if (!File.Exists(cachedPath) || new FileInfo(cachedPath).Length < 1)
                    {
                        var produced = await SynthesizeChunkWithFallbackAsync(
                            reference,
                            referenceAudioCache,
                            chunks[chunkIndex],
                            cachedPath,
                            workDirectory,
                            cancellationToken);
                        if (!produced)
                        {
                            completed++;
                            continue;
                        }
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

    /// <summary>
    /// Synthesizes one chunk into <paramref name="cachedPath"/>. A chunk the gateway rejects twice
    /// is retried sentence by sentence so a single unsynthesizable fragment (a stray header, a
    /// symbol-only line) is dropped instead of failing the whole book. Returns false when nothing
    /// speakable could be produced for the chunk.
    /// </summary>
    private async Task<bool> SynthesizeChunkWithFallbackAsync(
        LocalCloneVoiceReference reference,
        Dictionary<string, byte[]> referenceAudioCache,
        string text,
        string cachedPath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var audio = await LoadReferenceAudioAsync(reference, referenceAudioCache, cancellationToken);
        var stem = Path.GetFileNameWithoutExtension(cachedPath);
        var wholePath = Path.Combine(workDirectory, $"{stem}-whole.mp3");
        if (await TrySynthesizePieceAsync(reference, audio, text, wholePath, workDirectory, attempts: 2, cancellationToken))
        {
            File.Move(wholePath, cachedPath, overwrite: true);
            return true;
        }

        var pieces = SplitSentences(text).Where(HasSpeakableText).ToArray();
        var parts = new List<string>();
        var rejected = 0;
        for (var index = 0; index < pieces.Length; index++)
        {
            var piecePath = Path.Combine(workDirectory, $"{stem}-piece{index:000}.mp3");
            if (await TrySynthesizePieceAsync(reference, audio, pieces[index], piecePath, workDirectory, attempts: 2, cancellationToken))
            {
                parts.Add(piecePath);
            }
            else
            {
                rejected++;
            }
        }

        if (parts.Count == 0)
        {
            if (pieces.Length == 0)
            {
                return false;
            }

            // Every sentence failed: this looks like an outage rather than bad text. Keep
            // waiting for the gateway before giving up on the job.
            for (var attempt = 3; attempt <= MaxGatewayAttempts; attempt++)
            {
                await Task.Delay(GatewayRetryDelay, cancellationToken);
                if (await TrySynthesizePieceAsync(reference, audio, text, wholePath, workDirectory, attempts: 1, cancellationToken))
                {
                    File.Move(wholePath, cachedPath, overwrite: true);
                    return true;
                }
            }

            throw new InvalidOperationException("自架克隆聲線 gateway 持續無法合成。");
        }

        if (rejected > 0)
        {
            logger.LogWarning(
                "Local clone dropped {Rejected} of {Total} sentence(s) the gateway could not synthesize in one chunk",
                rejected,
                pieces.Length);
        }

        if (parts.Count == 1)
        {
            File.Move(parts[0], cachedPath, overwrite: true);
        }
        else
        {
            var joinedPath = Path.Combine(workDirectory, $"{stem}-joined.mp3");
            await ConcatAsync(parts, joinedPath, cancellationToken);
            File.Move(joinedPath, cachedPath, overwrite: true);
            foreach (var part in parts)
            {
                File.Delete(part);
            }
        }

        return true;
    }

    private static async Task<byte[]> LoadReferenceAudioAsync(
        LocalCloneVoiceReference reference,
        Dictionary<string, byte[]> referenceAudioCache,
        CancellationToken cancellationToken)
    {
        if (referenceAudioCache.TryGetValue(reference.ReferenceAudioPath, out var audio))
        {
            return audio;
        }

        if (!File.Exists(reference.ReferenceAudioPath))
        {
            throw new PermanentNarrationProviderException(
                MissingReferenceCode,
                "自架克隆聲線的參考錄音檔案不存在。");
        }

        audio = await File.ReadAllBytesAsync(reference.ReferenceAudioPath, cancellationToken);
        referenceAudioCache[reference.ReferenceAudioPath] = audio;
        return audio;
    }

    private async Task<bool> TrySynthesizePieceAsync(
        LocalCloneVoiceReference reference,
        byte[] audio,
        string text,
        string outputPath,
        string workDirectory,
        int attempts,
        CancellationToken cancellationToken)
    {
        LocalCloneGatewayAudio? result = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result = await client.SynthesizeAsync(
                    new LocalCloneGatewayRequest(text, reference.ReferenceTranscript, audio),
                    cancellationToken);
                break;
            }
            catch (LocalClonePreviewUnavailableException exception)
            {
                logger.LogWarning(
                    exception,
                    "Local clone gateway rejected a piece of {Length} chars (attempt {Attempt}/{Attempts})",
                    text.Length,
                    attempt,
                    attempts);
                if (attempt < attempts)
                {
                    await Task.Delay(GatewayRetryDelay, cancellationToken);
                }
            }
        }

        if (result is null)
        {
            return false;
        }

        var rawPath = Path.Combine(workDirectory, $"{Path.GetFileNameWithoutExtension(outputPath)}-raw.wav");
        await File.WriteAllBytesAsync(rawPath, result.Content, cancellationToken);
        await TranscodeAsync(rawPath, outputPath, cancellationToken);
        File.Delete(rawPath);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1)
        {
            throw new InvalidOperationException("自架克隆聲線音檔轉檔失敗。");
        }

        return true;
    }

    /// <summary>True when the text contains at least one CJK ideograph, bopomofo, kana or hangul
    /// character — anything else (Latin headers, digits, symbols) yields no CosyVoice output.</summary>
    internal static bool HasSpeakableText(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if ((value >= 0x4E00 && value <= 0x9FFF)
                || (value >= 0x3400 && value <= 0x4DBF)
                || (value >= 0x20000 && value <= 0x2FA1F)
                || (value >= 0xF900 && value <= 0xFAFF)
                || (value >= 0x3100 && value <= 0x312F)
                || (value >= 0x31A0 && value <= 0x31BF)
                || (value >= 0x3040 && value <= 0x30FF)
                || (value >= 0xAC00 && value <= 0xD7AF))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> SplitSentences(string text)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (var character in text)
        {
            current.Append(character);
            if ("。！？；!?;\n".Contains(character))
            {
                var piece = current.ToString().Trim();
                if (piece.Length > 0)
                {
                    pieces.Add(piece);
                }

                current.Clear();
            }
        }

        var tail = current.ToString().Trim();
        if (tail.Length > 0)
        {
            pieces.Add(tail);
        }

        return pieces;
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

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
// Integrated from the owner-authorized host source, SHA256
// 0990cc2b6bf85365345484056bbee39694dd4721f28be2688a091749e7f33e06.
public sealed class LocalCloneMultiVoiceNarrationProvider(
    ILocalCloneGatewayClient client,
    IOptions<LocalClonePreviewOptions> localCloneOptions,
    IOptions<NarrationOptions> narrationOptions,
    ILogger<LocalCloneMultiVoiceNarrationProvider> logger) : IVersionedMultiVoiceNarrationProvider
{
    public const string UnavailableCode = "local_clone_formal_narration_disabled";
    public const string MissingReferenceCode = "local_clone_reference_missing";

    // CosyVoice3 quality drops on very long single utterances and the gateway caps output at
    // 300 s, so keep chunks to roughly one paragraph.
    private const int MaxChars = 160;
    private const int MaxGatewayAttempts = 6;
    private TimeSpan GatewayRetryDelay => TimeSpan.FromSeconds(localCloneOptions.Value.RetryDelaySeconds);
    private const string BreakCharacters = "\n。！？；，、,.!?:：";

    public string ProviderName => CharacterVoiceProviders.LocalClone;
    public string ProviderVersion => LocalClonePreviewOptions.FormalProviderVersion;

    public async Task<MultiVoiceSynthesisResult> SynthesizeAsync(
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
        var integrity = new LocalCloneIntegrityReceipt(request.CacheContext, request.Turns.Sum(turn => turn.Text.Length));
        Directory.CreateDirectory(cacheDirectory);
        var referenceAudioCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            // Validate actual reference bytes even when resuming cached synthesized chunks.
            foreach (var key in resolvedTurns.Select(turn => turn.ReferenceKey).Distinct(StringComparer.Ordinal))
                await LoadReferenceAudioAsync(references[key], referenceAudioCache, workDirectory, cancellationToken);
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
                    var chunk = chunks[chunkIndex];
                    var evidence = new LocalCloneChunkEvidence(turnIndex, chunkIndex, chunk.Length,
                        Hash(Encoding.UTF8.GetBytes(chunk)), "pending", null, null);
                    integrity.Chunks.Add(evidence);
                    await WriteReceiptAsync(Path.Combine(cacheDirectory, "integrity.json"), integrity, cancellationToken);
                    if (!HasSpeakableText(chunk))
                    {
                        // Only whitespace and Unicode punctuation may be omitted, explicitly.
                        evidence.Disposition = "punctuation-only";
                        completed++;
                        continue;
                    }

                    var cachedPath = Path.Combine(cacheDirectory, $"{ChunkCacheKey(referenceKey, chunks[chunkIndex])}.mp3");
                    if (!await CacheMatchesAsync(cachedPath, cancellationToken))
                    {
                        var produced = await SynthesizeChunkWithFallbackAsync(
                            reference,
                            referenceAudioCache,
                            chunks[chunkIndex],
                            cachedPath,
                            workDirectory,
                            cancellationToken);
                        if (!produced) throw Incomplete();
                        await File.WriteAllTextAsync(cachedPath + ".sha256",
                            await HashFileAsync(cachedPath, cancellationToken), cancellationToken);
                    }

                    evidence.Disposition = "synthesized";
                    evidence.AudioSha256 = await HashFileAsync(cachedPath, cancellationToken);
                    evidence.AudioBytes = new FileInfo(cachedPath).Length;
                    await WriteReceiptAsync(Path.Combine(cacheDirectory, "integrity.json"), integrity, cancellationToken);
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

            integrity.Status = "complete";
            integrity.AudioSha256 = await HashFileAsync(candidate, cancellationToken);
            integrity.AudioBytes = new FileInfo(candidate).Length;
            await WriteReceiptAsync(outputPath + ".integrity.json", integrity, cancellationToken);
            File.Copy(candidate, outputPath, overwrite: false);
            TryDeleteDirectory(cacheDirectory, logger);
            return MultiVoiceSynthesisResult.None;
        }
        catch
        {
            integrity.Status = cancellationToken.IsCancellationRequested ? "cancelled" : "incomplete";
            await WriteReceiptAsync(Path.Combine(cacheDirectory, "integrity.json"), integrity, CancellationToken.None);
            throw;
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
        // Scope includes every immutable job identity plus reference bytes and transcript.
        // A new version cannot reuse old potentially partial cache entries.
        var identity = JsonSerializer.Serialize(new {
            Version = ProviderVersion, request.CacheContext,
            References = (request.CloneReferences ?? new Dictionary<string, LocalCloneVoiceReference>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, pair.Value.ReferenceAudioSha256,
                    Transcript = Hash(Encoding.UTF8.GetBytes(pair.Value.ReferenceTranscript)) })
        });
        return Path.Combine(narrationOptions.Value.AudioRootPath, "local-clone-cache-v2",
            Hash(Encoding.UTF8.GetBytes(identity)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
    private static async Task<bool> CacheMatchesAsync(string path, CancellationToken token) =>
        File.Exists(path) && new FileInfo(path).Length > 0 && File.Exists(path + ".sha256")
        && string.Equals(await File.ReadAllTextAsync(path + ".sha256", token),
            await HashFileAsync(path, token), StringComparison.Ordinal);
    private static Task WriteReceiptAsync(string path, LocalCloneIntegrityReceipt receipt, CancellationToken token) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(receipt), token);
    private static PermanentNarrationProviderException Incomplete() => new(
        "local_clone_incomplete_audio", "自架聲線有文字未成功合成；已保留進度，不會發布缺漏的音訊。");

    private static string ChunkCacheKey(string referenceKey, string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"{referenceKey}\n{text}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Synthesizes one chunk into <paramref name="cachedPath"/>. A chunk the gateway rejects twice
    /// is retried sentence by sentence. Every meaningful piece must succeed before caching;
    /// partial audio is never accepted as a completed chunk.
    /// </summary>
    private async Task<bool> SynthesizeChunkWithFallbackAsync(
        LocalCloneVoiceReference reference,
        Dictionary<string, byte[]> referenceAudioCache,
        string text,
        string cachedPath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var audio = await LoadReferenceAudioAsync(reference, referenceAudioCache, workDirectory, cancellationToken);
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

        if (rejected > 0) throw Incomplete();

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

    // The gateway (and CosyVoice's prompt extractor) reject prompts longer than 30 s. Stored
    // profiles may sit right at the limit, so anything over this is trimmed before upload.
    internal const double MaximumReferenceSeconds = 29.5;
    private const int ReferenceSampleRate = 48_000;
    private const int ReferenceBytesPerSecond = ReferenceSampleRate * 2;

    private async Task<byte[]> LoadReferenceAudioAsync(
        LocalCloneVoiceReference reference,
        Dictionary<string, byte[]> referenceAudioCache,
        string workDirectory,
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

        if (new FileInfo(reference.ReferenceAudioPath).Length > LocalClonePreviewOptions.MaximumReferenceAudioBytes)
            throw new PermanentNarrationProviderException(MissingReferenceCode, "參考錄音超過大小限制。");
        audio = await File.ReadAllBytesAsync(reference.ReferenceAudioPath, cancellationToken);
        if (!string.Equals(Hash(audio), reference.ReferenceAudioSha256, StringComparison.Ordinal))
            throw new PermanentNarrationProviderException(MissingReferenceCode, "參考錄音已變更，請重新確認聲線。");
        var seconds = EstimateWavSeconds(audio);
        if (seconds > MaximumReferenceSeconds)
        {
            var trimmedPath = Path.Combine(workDirectory, $"ref-{Guid.NewGuid():N}.wav");
            await RunFfmpegAsync(
                [
                    "-y", "-i", reference.ReferenceAudioPath, "-t",
                    MaximumReferenceSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                    "-ac", "1", "-ar", ReferenceSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-sample_fmt", "s16", "-fflags", "+bitexact", "-flags:a", "+bitexact",
                    "-map_metadata", "-1", "-f", "wav", trimmedPath
                ],
                cancellationToken);
            audio = await File.ReadAllBytesAsync(trimmedPath, cancellationToken);
            File.Delete(trimmedPath);
            logger.LogInformation(
                "Local clone trimmed a {Seconds:F2}s reference recording to {Limit:F1}s for the gateway",
                seconds,
                MaximumReferenceSeconds);
        }

        referenceAudioCache[reference.ReferenceAudioPath] = audio;
        return audio;
    }

    /// <summary>Duration of a 48 kHz mono PCM16 WAV from its <c>data</c> chunk; 0 when the
    /// container cannot be parsed (the gateway then reports the exact contract failure).</summary>
    internal static double EstimateWavSeconds(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return 0;
        }

        var position = 12;
        while (position + 8 <= wav.Length)
        {
            var chunkId = wav.AsSpan(position, 4);
            var chunkSize = BitConverter.ToUInt32(wav, position + 4);
            if (chunkId.SequenceEqual("data"u8))
            {
                var available = Math.Min((long)chunkSize, wav.Length - position - 8);
                return available / (double)ReferenceBytesPerSecond;
            }

            position += 8 + (int)Math.Min(chunkSize + (chunkSize & 1), (uint)(wav.Length - position - 8));
        }

        return 0;
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

    /// <summary>Only Unicode punctuation and whitespace may be omitted without synthesis.</summary>
    internal static bool HasSpeakableText(string text) =>
        text.EnumerateRunes().Any(rune => !Rune.IsWhiteSpace(rune) && !Rune.IsPunctuation(rune));

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
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 2);
        var remaining = text;
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
            if (cut < remaining.Length && char.IsHighSurrogate(remaining[cut - 1])
                && char.IsLowSurrogate(remaining[cut])) cut--;
            var chunk = remaining[..cut];
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            remaining = remaining[cut..];
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
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
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

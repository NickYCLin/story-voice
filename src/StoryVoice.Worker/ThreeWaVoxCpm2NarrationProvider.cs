using System.Diagnostics;
using StoryVoice.Domain.Narrations;
using StoryVoice.Infrastructure.Narrations;

namespace StoryVoice.Worker;

/// <summary>
/// Multi-character synthesis provider backed by the 3wa Cluster API's VoxCPM2 clone engine.
/// Each <see cref="NarrationTurn.Voice"/> is either an opaque reference produced by
/// <c>MultiCharacterTurnBuilder.EncodeVoiceReference</c> — <c>clone:&lt;task_id&gt;</c> (a confirmed
/// <see cref="CharacterVoiceProfileMode.Clone"/> profile, replayed via its 3wa task id) — or, for
/// characters (and the narrator — voice cloning isn't wired up for the
/// narrator yet, so it always stays on Edge; see <c>SeriesNarrationService.EnsureSingleSynthesisProvider</c>)
/// that stayed on <c>edge</c>, a literal Edge voice id delegated to the same
/// <c>edge_tts_provider.py</c> script the plain Edge provider uses. Every turn is synthesized one
/// chunk at a time, then concatenated with ffmpeg — the same concat-demuxer approach
/// <c>edge_tts_multi_voice_provider.py</c> uses, so downstream audio handling doesn't need to care
/// which provider (or mix of providers) rendered a job.
/// </summary>
public sealed class ThreeWaVoxCpm2NarrationProvider(
    IThreeWaSynthesisClient client,
    ILogger<ThreeWaVoxCpm2NarrationProvider> logger) : IMultiVoiceNarrationProvider
{
    public string ProviderName => CharacterVoiceProviders.ThreeWaVoxCpm2;

    private const int MaxChars = 4_000;
    private const int PollIntervalMs = 2_000;
    private const int MaxPollAttempts = 150;
    private const string BreakCharacters = "\n。！？；，、,.!?:：";

    public async Task<MultiVoiceSynthesisResult> SynthesizeAsync(
        MultiVoiceNarrationRequest request,
        string outputPath,
        Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Turns.Count == 0)
        {
            throw new ArgumentException("多角色語音 manifest 至少要有一個 turn。", nameof(request));
        }

        foreach (var turn in request.Turns)
        {
            if (string.IsNullOrWhiteSpace(turn.Text) || string.IsNullOrWhiteSpace(turn.Voice))
            {
                throw new ArgumentException("每個 turn 都必須有文字與聲線參照。", nameof(request));
            }
        }

        // Validate every voice reference before creating a work directory or submitting the first
        // chunk. A later Design turn must not be discovered after earlier Clone turns have already
        // caused non-idempotent provider work.
        var parsedReferences = request.Turns
            .Select(turn => ParseVoiceReference(turn.Voice))
            .ToArray();

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(outputDirectory);
        var workDirectory = Path.Combine(outputDirectory, $"3wa-voxcpm2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        try
        {
            var turnChunks = request.Turns
                .Select(turn => (Turn: turn, Chunks: SplitText(turn.Text, MaxChars)))
                .ToArray();
            var totalChunks = turnChunks.Sum(entry => entry.Chunks.Count);
            if (totalChunks == 0)
            {
                throw new InvalidOperationException("3wa 聲線合成沒有可用文字。");
            }

            var sequence = new List<string>();
            var completed = 0;
            for (var turnIndex = 0; turnIndex < turnChunks.Length; turnIndex++)
            {
                var (turn, chunks) = turnChunks[turnIndex];
                var reference = parsedReferences[turnIndex];

                if (turn.PauseBeforeMs > 0)
                {
                    var silencePath = Path.Combine(workDirectory, $"{turnIndex:0000}-pause.mp3");
                    await GenerateSilenceAsync(silencePath, turn.PauseBeforeMs, cancellationToken);
                    sequence.Add(silencePath);
                }

                for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var partPath = Path.Combine(workDirectory, $"{turnIndex:0000}-{chunkIndex:0000}.mp3");
                    await SynthesizeChunkAsync(reference, turn, chunks[chunkIndex], partPath, workDirectory, cancellationToken);
                    sequence.Add(partPath);
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
                throw new InvalidOperationException("3wa 聲線合成沒有產生可用音訊。");
            }

            File.Copy(candidate, outputPath, overwrite: true);
        }
        finally
        {
            TryDeleteDirectory(workDirectory, logger);
        }

        return MultiVoiceSynthesisResult.None;
    }

    private async Task SynthesizeChunkAsync(
        ParsedVoiceReference reference,
        NarrationTurn turn,
        string text,
        string outputPartPath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        if (reference.Kind == VoiceReferenceKind.Edge)
        {
            // edge-tts 輸出 24 kHz MP3；先落地再統一轉碼成 44.1 kHz，
            // 否則 -c copy 串接會產生取樣率混雜的破碎輸出。
            var edgeRawPath = Path.Combine(
                workDirectory,
                $"{Path.GetFileNameWithoutExtension(outputPartPath)}-edge-raw.mp3");
            await SynthesizeEdgeFallbackChunkAsync(
                reference.EdgeVoice!,
                turn.Rate,
                turn.Pitch,
                turn.Volume,
                text,
                edgeRawPath,
                cancellationToken);
            await TranscodeAsync(edgeRawPath, outputPartPath, cancellationToken);
            File.Delete(edgeRawPath);
            if (!File.Exists(outputPartPath) || new FileInfo(outputPartPath).Length < 1)
            {
                throw new InvalidOperationException("edge-tts 備援音檔轉檔失敗。");
            }

            return;
        }

        var handle = await client.SubmitAsync(
            new ThreeWaSynthesisRequest(text, "ultimate_clone", reference.TaskId, VoicePromptText: null),
            cancellationToken);

        string status;
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            status = await client.GetTaskStatusAsync(handle.StatusUrl, cancellationToken);
            if (IsTerminalStatus(status))
            {
                break;
            }

            attempt++;
            if (attempt > MaxPollAttempts)
            {
                throw new InvalidOperationException("3wa 聲線合成逾時未完成。");
            }

            await Task.Delay(PollIntervalMs, cancellationToken);
        }

        if (!IsSuccessStatus(status))
        {
            logger.LogWarning("3wa VoxCPM2 synthesis task finished with non-success status {Status}", status);
            throw new InvalidOperationException("3wa 聲線合成失敗。");
        }

        var artifacts = await client.GetResultArtifactsAsync(handle.ResultUrl, cancellationToken);
        var artifact = artifacts.FirstOrDefault()
            ?? throw new InvalidOperationException("3wa 聲線合成沒有回傳可用的音訊 artifact。");
        if (string.IsNullOrWhiteSpace(handle.ArtifactUrlTemplate))
        {
            throw new InvalidOperationException("3wa 聲線合成沒有回傳 artifact_url_template。");
        }

        var rawPath = Path.Combine(workDirectory, $"{Path.GetFileNameWithoutExtension(outputPartPath)}-raw.bin");
        await using (var rawStream = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await client.DownloadArtifactAsync(handle.ArtifactUrlTemplate, artifact.Id, rawStream, cancellationToken);
        }

        await TranscodeAsync(rawPath, outputPartPath, cancellationToken);
        File.Delete(rawPath);

        if (!File.Exists(outputPartPath) || new FileInfo(outputPartPath).Length < 1)
        {
            throw new InvalidOperationException("3wa 聲線合成音檔轉檔失敗。");
        }
    }

    private enum VoiceReferenceKind
    {
        Clone,
        Edge,
    }

    private sealed record ParsedVoiceReference(VoiceReferenceKind Kind, string? TaskId, string? EdgeVoice);

    /// <summary>
    /// A bare (unprefixed) voice reference is a literal Edge voice id — either a character that
    /// stayed on <c>edge</c> while castmates use 3wa profiles, or the series narrator, whose own
    /// voice cloning isn't wired up yet and so always resolves to <c>castRevision.NarratorVoice</c>
    /// verbatim (see <c>MultiCharacterTurnBuilder.ResolveVoice</c>'s narrator fallback).
    /// </summary>
    private static ParsedVoiceReference ParseVoiceReference(string voiceReference)
    {
        if (voiceReference.StartsWith("clone:", StringComparison.Ordinal))
        {
            var taskId = voiceReference["clone:".Length..];
            if (string.IsNullOrWhiteSpace(taskId))
            {
                throw new InvalidOperationException("3wa 克隆聲線參照缺少 task id。");
            }

            return new ParsedVoiceReference(VoiceReferenceKind.Clone, taskId, null);
        }

        if (voiceReference.StartsWith("design:", StringComparison.Ordinal))
        {
            throw new PermanentNarrationProviderException(
                ThreeWaSynthesisCapabilities.DesignVoiceUnavailableCode,
                ThreeWaSynthesisCapabilities.DesignVoiceUnavailableMessage);
        }

        if (string.Equals(voiceReference, "custom", StringComparison.OrdinalIgnoreCase))
        {
            throw new PermanentNarrationProviderException(
                ThreeWaSynthesisCapabilities.LegacyNarratorVoiceUnavailableCode,
                ThreeWaSynthesisCapabilities.LegacyNarratorVoiceUnavailableMessage);
        }

        return new ParsedVoiceReference(VoiceReferenceKind.Edge, null, voiceReference);
    }

    private static async Task SynthesizeEdgeFallbackChunkAsync(
        string voice,
        string rate,
        string pitch,
        string volume,
        string text,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "edge_tts_provider.py");
        var startInfo = new ProcessStartInfo("python3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--voice");
        startInfo.ArgumentList.Add(voice);
        startInfo.ArgumentList.Add($"--rate={rate}");
        startInfo.ArgumentList.Add($"--pitch={pitch}");
        startInfo.ArgumentList.Add($"--volume={volume}");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("無法啟動 edge-tts 備援 provider。");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        _ = await stdoutTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"edge-tts 備援 provider 執行失敗（結束碼 {process.ExitCode}）：{stderr}");
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1)
        {
            throw new InvalidOperationException("edge-tts 備援 provider 沒有產生音訊。");
        }
    }

    private static bool IsTerminalStatus(string status) =>
        IsSuccessStatus(status) || status is "failed" or "error" or "cancelled" or "canceled";

    private static bool IsSuccessStatus(string status) =>
        status is "succeeded" or "success" or "completed";

    private static IReadOnlyList<string> SplitText(string text, int maxChars)
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

    // 固定 44100 Hz／mono：串接時是 -c copy，所有片段（含 anullsrc 靜音）的
    // codec 參數必須一致，否則混入 24 kHz 的 edge 退回片段會產生破碎輸出。
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
            logger.LogWarning(exception, "Unable to remove a 3wa VoxCPM2 working directory");
        }
    }
}

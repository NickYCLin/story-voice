using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace StoryVoice.Worker;

public sealed class EdgeTtsMultiVoiceNarrationProvider(
    ILogger<EdgeTtsMultiVoiceNarrationProvider> logger,
    IOptions<EdgeTtsOptions>? options = null)
    : IMultiVoiceNarrationProvider
{
    private const string ManifestSchemaVersion = "storyvoice:multi-voice-manifest:v1";
    internal const string TimelineSchemaVersion = "storyvoice:multi-voice-timeline:v1";
    private static readonly JsonSerializerOptions ManifestSerializerOptions = new(JsonSerializerDefaults.Web);

    public string ProviderName => "edge";

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
            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                throw new ArgumentException("每個 turn 的文字都不可為空白。", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(turn.Voice))
            {
                throw new ArgumentException("每個 turn 都必須指定聲線。", nameof(request));
            }
        }

        var manifestJson = JsonSerializer.Serialize(
            new Manifest(ManifestSchemaVersion, request.Turns),
            ManifestSerializerOptions);

        EdgeTtsNarrationProvider.CleanupTemporaryDirectories(outputPath, logger);
        var startInfo = CreateStartInfo(outputPath);

        using var process = new Process { StartInfo = startInfo };
        string providerOutput;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("無法啟動多角色神經語音 provider。");
            }

            var stderrTask = ReadDiagnosticsAsync(process.StandardError, progressCallback, cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            try
            {
                await process.StandardInput.WriteAsync(manifestJson.AsMemory(), cancellationToken);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);
                var diagnostics = await stderrTask;
                providerOutput = await stdoutTask;

                if (process.ExitCode != 0)
                {
                    // The provider's error messages only ever reference turn/chunk indices and
                    // tool exit codes (see edge_tts_multi_voice_provider.py) — never the
                    // synthesized text itself — so the tail is safe to log for diagnosis.
                    logger.LogWarning(
                        "Multi-voice Edge TTS provider exited with code {ExitCode}; diagnostic tail: {DiagnosticTail}",
                        process.ExitCode,
                        diagnostics.Tail);
                    throw new InvalidOperationException("多角色神經語音 provider 執行失敗。");
                }
            }
            catch
            {
                await TerminateAsync(process);
                await IgnoreFailureAsync(stderrTask);
                await IgnoreFailureAsync(stdoutTask);
                throw;
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1)
            {
                throw new InvalidOperationException("多角色神經語音 provider 沒有產生音訊。");
            }
        }
        finally
        {
            EdgeTtsNarrationProvider.CleanupTemporaryDirectories(outputPath, logger);
        }

        var turnTimings = TryParseTurnTimings(providerOutput, request.Turns.Count);
        if (turnTimings is null)
        {
            // Timing is best-effort telemetry about audio that already exists — a malformed or
            // missing timeline must never fail the finished synthesis.
            logger.LogWarning(
                "Multi-voice Edge TTS provider completed without a usable turn timeline; playback sync is unavailable for this job");
        }

        return new MultiVoiceSynthesisResult(turnTimings);
    }

    internal ProcessStartInfo CreateStartInfo(string outputPath)
    {
        var concurrency = options?.Value.MaximumConcurrentChunks ?? 1;
        if (concurrency is < 1 or > 4)
            throw new InvalidOperationException("Edge TTS chunks per job must use between 1 and 4 concurrent slots.");
        var startInfo = new ProcessStartInfo("python3")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "edge_tts_multi_voice_provider.py"));
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--max-concurrent-chunks");
        startInfo.ArgumentList.Add(concurrency.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    internal static IReadOnlyList<NarrationTurnTiming>? TryParseTurnTimings(
        string? providerOutput,
        int expectedTurnCount)
    {
        if (string.IsNullOrWhiteSpace(providerOutput))
        {
            return null;
        }

        TimelineOutput? timeline;
        try
        {
            timeline = JsonSerializer.Deserialize<TimelineOutput>(
                providerOutput.Trim(),
                ManifestSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (timeline is null
            || !string.Equals(timeline.SchemaVersion, TimelineSchemaVersion, StringComparison.Ordinal)
            || timeline.Turns is null
            || timeline.Turns.Count != expectedTurnCount)
        {
            return null;
        }

        var timings = new NarrationTurnTiming[expectedTurnCount];
        var previousEndMs = 0L;
        for (var index = 0; index < expectedTurnCount; index++)
        {
            var turn = timeline.Turns[index];
            if (turn is null
                || turn.Index != index
                || turn.StartMs < previousEndMs
                || turn.DurationMs < 0)
            {
                return null;
            }

            timings[index] = new NarrationTurnTiming(index, turn.StartMs, turn.DurationMs);
            previousEndMs = turn.StartMs + turn.DurationMs;
        }

        return timings;
    }

    private sealed record TimelineOutput(
        [property: JsonPropertyName("schemaVersion")] string? SchemaVersion,
        [property: JsonPropertyName("turns")] IReadOnlyList<TimelineTurnOutput?>? Turns);

    private sealed record TimelineTurnOutput(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("startMs")] long StartMs,
        [property: JsonPropertyName("durationMs")] long DurationMs);

    private const int DiagnosticTailLines = 30;

    private static async Task<(int Length, string Tail)> ReadDiagnosticsAsync(
        StreamReader reader,
        Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var diagnosticLength = 0;
        var tail = new Queue<string>(DiagnosticTailLines + 1);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            diagnosticLength += line.Length + 1;
            tail.Enqueue(line);
            if (tail.Count > DiagnosticTailLines)
            {
                tail.Dequeue();
            }

            if (progressCallback is not null && EdgeTtsNarrationProvider.TryParseProgress(line, out var progress))
            {
                await progressCallback(progress, cancellationToken);
            }
        }

        return (diagnosticLength, string.Join(" | ", tail));
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record Manifest(
        [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
        [property: JsonPropertyName("turns")] IReadOnlyList<NarrationTurn> Turns);
}

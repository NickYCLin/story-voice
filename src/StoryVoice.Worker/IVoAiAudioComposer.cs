namespace StoryVoice.Worker;

public sealed record VoAiAudioSegment(
    string InputWavPath,
    string Volume,
    int PauseBeforeMs,
    int TurnIndex = 0);

/// <summary>A provider-neutral WAV segment consumed by the shared ffmpeg composition seam.</summary>
public sealed record FfmpegAudioSegment(
    string InputWavPath,
    string Volume,
    int PauseBeforeMs,
    bool DeleteInputAfterNormalization = true,
    int TurnIndex = 0);

public interface IVoAiAudioComposer
{
    Task<MultiVoiceSynthesisResult> ComposeAsync(
        IReadOnlyList<VoAiAudioSegment> segments,
        string outputPath,
        CancellationToken cancellationToken);
}

public interface IFfmpegAudioComposer
{
    Task<MultiVoiceSynthesisResult> ComposeAsync(
        IReadOnlyList<FfmpegAudioSegment> segments,
        string outputPath,
        int outputSampleRate,
        CancellationToken cancellationToken);
}

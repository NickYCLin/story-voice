namespace StoryVoice.Worker;

public sealed class AudioComposerOptions
{
    public const string SectionName = "AudioComposer";

    public bool EnableLoudnessNormalization { get; set; } = true;

    public double TargetIntegratedLoudness { get; set; } = -16.0;

    public double TargetTruePeak { get; set; } = -1.5;

    public double TargetLoudnessRange { get; set; } = 11.0;
}

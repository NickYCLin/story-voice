namespace StoryVoice.Worker;

public sealed class VoAiOptions
{
    public const string SectionName = "VoAi";

    public string BaseUrl { get; set; } = "https://connect.voai.ai/";

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 300;

    public long MaximumResponseBytes { get; set; } = 64L * 1024 * 1024;

    public long MaximumJobResponseBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public int MaximumChunksPerJob { get; set; } = 5_000;

    public int MaximumConcurrentChunks { get; set; } = 1;

    public int SampleRate { get; set; } = 32_000;
}

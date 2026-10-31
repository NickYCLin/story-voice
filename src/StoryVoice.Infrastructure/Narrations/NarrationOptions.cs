namespace StoryVoice.Infrastructure.Narrations;

public sealed class NarrationOptions
{
    public const string SectionName = "Narration";

    public string AudioRootPath { get; set; } = "/data/audio";
    public string Voice { get; set; } = "zh-TW-YunJheNeural";
    public string Rate { get; set; } = "-5%";
    public int ProviderTimeoutMinutes { get; set; } = 20;
    public int LeaseMinutes { get; set; } = 5;
    public int MaxAttempts { get; set; } = 3;
    public int MaximumConcurrentJobs { get; set; } = 1;
}

namespace StoryVoice.Worker;

public sealed class EdgeTtsOptions
{
    public const string SectionName = "EdgeTts";
    public int MaximumConcurrentChunks { get; set; } = 1;
}

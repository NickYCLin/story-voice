namespace StoryVoice.Worker;

public sealed class WorkerMetricsOptions
{
    public const string SectionName = "WorkerMetrics";

    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public int ExportIntervalSeconds { get; set; } = 30;
    public int ExportTimeoutSeconds { get; set; } = 5;
}

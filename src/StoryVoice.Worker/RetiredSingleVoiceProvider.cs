namespace StoryVoice.Worker;

public sealed class RetiredSingleVoiceProvider : INarrationProvider
{
    public Task SynthesizeAsync(string text, string outputPath, string voice, string rate,
        Func<NarrationSynthesisProgress, CancellationToken, Task>? progressCallback, CancellationToken cancellationToken) =>
        Task.FromException(new PermanentNarrationProviderException("character_voice_required",
            "Edge 單人產生功能已停用，請從角色管理選擇配音聲線。"));
}

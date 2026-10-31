using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StoryVoice.Application.Characters;
using StoryVoice.Application.Insights;
using StoryVoice.Infrastructure.Insights;

namespace StoryVoice.UnitTests;

public sealed class CharacterProfileAssistTests
{
    private static GenerateCharacterProfileAssistRequest Request(string field = "speakingStyle") =>
        new("小雨", "女", "20", "喜歡修理舊鐘", "住在海邊", null, "原本的說話風格", field);

    [Fact]
    public async Task Speaking_style_uses_the_model_result_and_exact_requested_schema_then_unloads()
    {
        var gate = new RecordingGpuExecutionGate();
        using var handler = new OllamaHandler("""{"speakingStyle":"說話簡短，談到鐘錶時會放慢語速。"}""");
        using var client = CreateClient(handler);
        var provider = CreateProvider(client, gate);
        var result = await provider.GenerateAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal("說話簡短，談到鐘錶時會放慢語速。", result.SpeakingStyle);
        Assert.Null(result.Personality);
        Assert.Null(result.Background);
        Assert.Null(result.Catchphrase);
        using var payload = JsonDocument.Parse(handler.ChatBody!);
        var root = payload.RootElement;
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("speakingStyle", root.GetProperty("format").GetProperty("required")[0].GetString());
        Assert.Single(root.GetProperty("format").GetProperty("properties").EnumerateObject());
        Assert.Contains("原本的說話風格", JsonDocument.Parse(root.GetProperty("messages")[1].GetProperty("content").GetString()!).RootElement.GetProperty("existingSpeakingStyle").GetString());
        Assert.Equal(1, handler.Unloads);
        Assert.True(gate.LastLease!.Disposed);
        Assert.False(gate.LastLease.Abandoned);
    }

    [Fact]
    public async Task Full_generation_rewrites_existing_fields_and_returns_all_four_fields()
    {
        using var handler = new OllamaHandler("""{"personality":"細心但怕生","background":"修理鐘錶的學徒","speakingStyle":"語句簡短","catchphrase":"我再看看。"}""");
        using var client = CreateClient(handler);
        var result = await CreateProvider(client, new RecordingGpuExecutionGate()).GenerateAsync(Request("all"), TestContext.Current.CancellationToken);
        Assert.Equal("細心但怕生", result.Personality);
        Assert.Equal("修理鐘錶的學徒", result.Background);
        Assert.Equal("語句簡短", result.SpeakingStyle);
        Assert.Equal("我再看看。", result.Catchphrase);
    }

    [Theory]
    [InlineData("speakingstyle")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task Invalid_fields_fail_before_acquiring_the_gpu(string field)
    {
        var gate = new RecordingGpuExecutionGate();
        using var handler = new OllamaHandler("{}");
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(client, gate).GenerateAsync(Request(field), TestContext.Current.CancellationToken));
        Assert.Null(gate.LastLease);
        Assert.Null(handler.ChatBody);
    }

    [Fact]
    public async Task Oversized_input_and_empty_name_fail_before_sending_private_material()
    {
        using var handler = new OllamaHandler("{}");
        using var client = CreateClient(handler);
        var provider = CreateProvider(client, new RecordingGpuExecutionGate());
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateAsync(Request() with { ExistingBackground = new string('字', 4001) }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateAsync(Request() with { CanonicalName = " " }, TestContext.Current.CancellationToken));
        Assert.Null(handler.ChatBody);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"speakingStyle\":null}")]
    [InlineData("{\"speakingStyle\":\" \"}")]
    [InlineData("{\"speakingStyle\":\"a\",\"speakingStyle\":\"b\"}")]
    [InlineData("{\"speakingStyle\":\"a\",\"background\":\"unexpected\"}")]
    [InlineData("not-json")]
    public async Task Malformed_model_output_is_rejected_without_template_fallback(string output)
    {
        using var handler = new OllamaHandler(output);
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<LocalLlmCharacterAnalysisUnavailableException>(() => CreateProvider(client, new RecordingGpuExecutionGate()).GenerateAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Unloads);
    }

    [Fact]
    public async Task Oversized_model_output_is_rejected_and_model_is_unloaded()
    {
        using var handler = new OllamaHandler(JsonSerializer.Serialize(new { speakingStyle = new string('字', 2001) }));
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<LocalLlmCharacterAnalysisUnavailableException>(() => CreateProvider(client, new RecordingGpuExecutionGate()).GenerateAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Unloads);
    }

    [Fact]
    public async Task Cancellation_still_unloads_with_an_independent_token()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new OllamaHandler("{}", onChat: () => cancellation.Cancel());
        using var client = CreateClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateProvider(client, new RecordingGpuExecutionGate()).GenerateAsync(Request(), cancellation.Token));
        Assert.Equal(1, handler.Unloads);
    }

    [Fact]
    public async Task Failed_unload_does_not_release_the_gpu_as_if_the_model_had_stopped()
    {
        var gate = new RecordingGpuExecutionGate();
        using var handler = new OllamaHandler("""{"speakingStyle":"簡短直接"}""", unloadStatus: HttpStatusCode.ServiceUnavailable);
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<LocalLlmCharacterAnalysisUnavailableException>(() => CreateProvider(client, gate).GenerateAsync(Request(), TestContext.Current.CancellationToken));
        Assert.True(gate.LastLease!.Abandoned);
        Assert.True(gate.LastLease.Disposed);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost/") };
    private static OllamaCharacterAnalysisProvider CreateProvider(HttpClient client, RecordingGpuExecutionGate gate) => new(client, Options.Create(new LocalLlmCharacterAnalysisOptions()), gate);

    private sealed class OllamaHandler(string content, Action? onChat = null, HttpStatusCode unloadStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? ChatBody { get; private set; }
        public int Unloads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/api/generate")
            {
                Unloads++;
                return new HttpResponseMessage(unloadStatus) { Content = new StringContent("{\"done\":true}", Encoding.UTF8, "application/json") };
            }
            Assert.Equal("/api/chat", request.RequestUri.AbsolutePath);
            ChatBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            onChat?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { done = true, message = new { content } }), Encoding.UTF8, "application/json") };
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryVoice.Application.Characters;
using StoryVoice.Application.Insights;

namespace StoryVoice.IntegrationTests;

public sealed class CharacterProfileAssistApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Assistance_requires_authentication_and_csrf_and_does_not_save_generated_fields()
    {
        var generator = new FakeGenerator();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICharacterProfileAssistGenerator>();
            services.AddSingleton<ICharacterProfileAssistGenerator>(generator);
        }));
        var ct = TestContext.Current.CancellationToken;
        var request = new GenerateCharacterProfileAssistRequest("小雨", null, null, null, null, null, null, "speakingStyle");
        using var anonymous = app.CreateClient();
        using var anonymousResponse = await anonymous.PostAsJsonAsync("/api/character-profiles/ai-assist", request, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        using var client = await app.CreateAuthenticatedClientAsync(ct);
        using var missingCsrf = await client.PostAsJsonAsync("/api/character-profiles/ai-assist", request, ct);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(0, generator.Calls);
        using var response = await client.PostWithCsrfAsync("/api/character-profiles/ai-assist", request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GeneratedCharacterProfileAssistResponse>(ct);
        Assert.Equal("語句簡短，尾音輕快。", result!.SpeakingStyle);
        Assert.Equal("speakingStyle", generator.LastRequest!.FieldToGenerate);
        var profiles = await client.GetFromJsonAsync<CharacterProfileResponse[]>("/api/character-profiles", ct);
        Assert.Empty(profiles!);
    }

    [Fact]
    public async Task Unavailable_model_returns_503_instead_of_a_successful_fixed_template()
    {
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICharacterProfileAssistGenerator>();
            services.AddSingleton<ICharacterProfileAssistGenerator>(new FakeGenerator { Unavailable = true });
        }));
        var ct = TestContext.Current.CancellationToken;
        using var client = await app.CreateAuthenticatedClientAsync(ct);
        using var response = await client.PostWithCsrfAsync("/api/character-profiles/ai-assist", new { canonicalName = "小雨", fieldToGenerate = "all" }, ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class FakeGenerator : ICharacterProfileAssistGenerator
    {
        public bool Unavailable { get; init; }
        public int Calls { get; private set; }
        public GenerateCharacterProfileAssistRequest? LastRequest { get; private set; }
        public Task<GeneratedCharacterProfileAssistResponse> GenerateAsync(GenerateCharacterProfileAssistRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (Unavailable) throw new LocalLlmCharacterAnalysisUnavailableException();
            return Task.FromResult(new GeneratedCharacterProfileAssistResponse(null, null, "語句簡短，尾音輕快。", null));
        }
    }
}

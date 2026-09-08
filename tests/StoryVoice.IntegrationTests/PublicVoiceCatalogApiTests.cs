using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using StoryVoice.Application.VoiceCatalog;

namespace StoryVoice.IntegrationTests;

public sealed class PublicVoiceCatalogApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Alias = "synthetic-story-voice";

    [Fact]
    public async Task Endpoints_are_not_mapped_when_catalog_is_disabled()
    {
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var catalog = await client.GetAsync("/api/public/v1/voices", cancellationToken);
        using var detail = await client.GetAsync($"/api/public/v1/voices/{Alias}", cancellationToken);
        using var demo = await client.GetAsync(
            $"/api/public/v1/voices/{Alias}/demo",
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, demo.StatusCode);
    }

    [Fact]
    public async Task Enabled_catalog_with_no_entries_returns_an_empty_array()
    {
        var assetRoot = Path.Combine(factory.StorageRoot, "public-catalog-empty");
        Directory.CreateDirectory(assetRoot);
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("VoiceCatalog:Enabled", "true");
            builder.UseSetting("VoiceCatalog:AssetRootPath", assetRoot);
        });
        using var client = configuredFactory.CreateClient();

        using var response = await client.GetAsync(
            "/api/public/v1/voices",
            TestContext.Current.CancellationToken);
        var cards = await response.Content.ReadFromJsonAsync<PublicVoiceCatalogCard[]>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(cards ?? []);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        foreach (var alias in new[] { Alias, "UNKNOWN", "unknown_voice", "-invalid", new string('a', 65) })
        {
            using var detail = await client.GetAsync($"/api/public/v1/voices/{alias}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
            Assert.Contains("no-store", detail.Headers.CacheControl?.ToString());
        }
    }
}

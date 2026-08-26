using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StoryVoice.IntegrationTests;

internal static class TestClientAuthentication
{
    public static HttpClient CreateCookieClient(this WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    public static async Task<HttpClient> CreateAuthenticatedClientAsync(
        this WebApplicationFactory<Program> factory,
        CancellationToken cancellationToken)
    {
        var client = factory.CreateCookieClient();
        await client.RegisterAsync(
            $"reader-{Guid.NewGuid():N}@example.com",
            "Moonlight!Story42",
            cancellationToken);
        return client;
    }

    public static async Task<HttpResponseMessage> SendWithCsrfAsync(
        this HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Add("X-CSRF-TOKEN", await client.GetCsrfTokenAsync(cancellationToken));
        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task<HttpResponseMessage> PostMultipartWithCsrfAsync(
        this HttpClient client,
        string path,
        MultipartFormDataContent body,
        CancellationToken cancellationToken)
    {
        var csrfToken = await client.GetCsrfTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task<string> GetCsrfTokenAsync(
        this HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/api/auth/session", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return body.RootElement.GetProperty("csrfToken").GetString()
            ?? throw new InvalidOperationException("Auth session did not return a CSRF token.");
    }

    public static async Task<HttpResponseMessage> PostWithCsrfAsync<T>(
        this HttpClient client,
        string path,
        T body,
        CancellationToken cancellationToken)
    {
        var csrfToken = await client.GetCsrfTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task<HttpResponseMessage> PutWithCsrfAsync<T>(
        this HttpClient client,
        string path,
        T body,
        CancellationToken cancellationToken)
    {
        var csrfToken = await client.GetCsrfTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task<HttpResponseMessage> DeleteWithCsrfAsync(
        this HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        var csrfToken = await client.GetCsrfTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task RegisterAsync(
        this HttpClient client,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostWithCsrfAsync(
            "/api/auth/register",
            new { email, password },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

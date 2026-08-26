using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StoryVoice.Application.Books;

namespace StoryVoice.IntegrationTests;

public sealed class AuthApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Anonymous_user_receives_session_csrf_but_cannot_read_books()
    {
        using var client = factory.CreateCookieClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var sessionResponse = await client.GetAsync("/api/auth/session", cancellationToken);
        using var session = JsonDocument.Parse(
            await sessionResponse.Content.ReadAsStreamAsync(cancellationToken));
        using var booksResponse = await client.GetAsync("/api/books", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        Assert.False(session.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(session.RootElement.GetProperty("csrfToken").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, booksResponse.StatusCode);
    }

    [Fact]
    public async Task Register_requires_csrf_and_creates_authenticated_session()
    {
        using var client = factory.CreateCookieClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"reader-{Guid.NewGuid():N}@example.com";

        using var missingCsrfResponse = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email, password = "Moonlight!Story42" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);

        using var registerResponse = await client.PostWithCsrfAsync(
            "/api/auth/register",
            new { email, password = "Moonlight!Story42" },
            cancellationToken);
        using var sessionResponse = await client.GetAsync("/api/auth/session", cancellationToken);
        using var session = JsonDocument.Parse(
            await sessionResponse.Content.ReadAsStreamAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.True(session.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(email, session.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Login_rejects_wrong_password_and_logout_clears_session()
    {
        using var client = factory.CreateCookieClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"reader-{Guid.NewGuid():N}@example.com";
        const string password = "Moonlight!Story42";
        await client.RegisterAsync(email, password, cancellationToken);

        using var logoutResponse = await client.PostWithCsrfAsync(
            "/api/auth/logout",
            new { },
            cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        using var wrongPasswordResponse = await client.PostWithCsrfAsync(
            "/api/auth/login",
            new { email, password = "Wrong!Password42", rememberMe = false },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);

        using var loginResponse = await client.PostWithCsrfAsync(
            "/api/auth/login",
            new { email, password, rememberMe = false },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task Books_are_isolated_between_storyvoice_accounts()
    {
        using var ownerClient = factory.CreateCookieClient();
        using var otherClient = factory.CreateCookieClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        await ownerClient.RegisterAsync(
            $"owner-{Guid.NewGuid():N}@example.com",
            "Moonlight!Story42",
            cancellationToken);
        await otherClient.RegisterAsync(
            $"other-{Guid.NewGuid():N}@example.com",
            "Moonlight!Story42",
            cancellationToken);

        using var createResponse = await ownerClient.PostWithCsrfAsync(
            "/api/books",
            new CreateBookRequest(
                "只屬於我的故事",
                "StoryVoice",
                "zh-TW",
                "private.txt",
                [new CreateChapterRequest(1, "第一章", "只有擁有者能看見。")]),
            cancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<BookDetailsResponse>(
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(created);

        var ownerBooks = await ownerClient.GetFromJsonAsync<BookDetailsResponse[]>(
            "/api/books",
            cancellationToken);
        using var otherListResponse = await otherClient.GetAsync("/api/books", cancellationToken);
        var otherBooks = await otherListResponse.Content.ReadFromJsonAsync<BookDetailsResponse[]>(
            cancellationToken);
        using var otherGetResponse = await otherClient.GetAsync(
            $"/api/books/{created.Id}",
            cancellationToken);

        Assert.Contains(ownerBooks!, book => book.Id == created.Id);
        Assert.DoesNotContain(otherBooks!, book => book.Id == created.Id);
        Assert.Equal(HttpStatusCode.NotFound, otherGetResponse.StatusCode);
    }

    [Fact]
    public async Task Retired_bookshelf_sync_routes_are_not_exposed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await factory.CreateAuthenticatedClientAsync(cancellationToken);

        using var tokenResponse = await client.PostWithCsrfAsync(
            "/api/auth/companion-token",
            new { },
            cancellationToken);
        using var importResponse = await client.PostWithCsrfAsync(
            "/api/books/sources/books-com-tw/import",
            new { books = Array.Empty<object>() },
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, tokenResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, importResponse.StatusCode);
    }

}

using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using StoryVoice.Infrastructure.Identity;

namespace StoryVoice.Api;

public static class StoryVoicePolicies
{
    public const string UserSession = "StoryVoiceUserSession";
    public const string ExternalVoiceSynthesis = "StoryVoiceExternalVoiceSynthesis";
}

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth").WithTags("Authentication");

        group.MapGet("/session", (
            HttpContext httpContext,
            IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            return Results.Ok(new
            {
                authenticated = httpContext.User.Identity?.IsAuthenticated == true,
                email = httpContext.User.FindFirstValue(ClaimTypes.Email),
                csrfToken = tokens.RequestToken
            });
        })
        .AllowAnonymous()
        .WithName("GetAuthSession");

        group.MapPost("/register", async (
            RegisterRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            CancellationToken cancellationToken) =>
        {
            var email = request.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["account"] = ["請輸入有效的電子郵件與密碼。"]
                });
            }

            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var result = await userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["account"] = result.Errors.Select(error => LocalizeIdentityError(error.Code)).ToArray()
                });
            }

            await signInManager.SignInAsync(user, isPersistent: false);
            return Results.Ok(new { authenticated = true, email = user.Email });
        })
        .AllowAnonymous()
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("RegisterAccount");

        group.MapPost("/login", async (
            LoginRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var email = request.Email?.Trim();
            var user = string.IsNullOrWhiteSpace(email)
                ? null
                : await userManager.FindByEmailAsync(email);
            if (user is null || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.Unauthorized();
            }

            var result = await signInManager.PasswordSignInAsync(
                user,
                request.Password,
                request.RememberMe,
                lockoutOnFailure: true);
            return result.Succeeded
                ? Results.Ok(new { authenticated = true, email = user.Email })
                : Results.Unauthorized();
        })
        .AllowAnonymous()
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("LoginAccount");

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.NoContent();
        })
        .RequireAuthorization(StoryVoicePolicies.UserSession)
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithName("LogoutAccount");

        return endpoints;
    }

    private static string LocalizeIdentityError(string code) => code switch
    {
        "DuplicateEmail" or "DuplicateUserName" => "這個電子郵件已經註冊。",
        "InvalidEmail" or "InvalidUserName" => "電子郵件格式不正確。",
        "PasswordTooShort" => "密碼至少需要 10 個字元。",
        "PasswordRequiresNonAlphanumeric" => "密碼至少需要一個符號。",
        "PasswordRequiresDigit" => "密碼至少需要一個數字。",
        "PasswordRequiresUpper" => "密碼至少需要一個大寫英文字母。",
        "PasswordRequiresLower" => "密碼至少需要一個小寫英文字母。",
        _ => "帳號資料不符合安全規則。"
    };

    private sealed record RegisterRequest(string? Email, string? Password);

    private sealed record LoginRequest(string? Email, string? Password, bool RememberMe);
}

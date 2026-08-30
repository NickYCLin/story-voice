using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using StackExchange.Redis;
using System.Threading.RateLimiting;
using StoryVoice.Api;
using StoryVoice.Application.Authentication;
using StoryVoice.Application.BookImports;
using StoryVoice.Application.Books;
using StoryVoice.Application.ExternalVoices;
using StoryVoice.Application.Narrations;
using StoryVoice.Infrastructure;
using StoryVoice.Infrastructure.ExternalVoices;
using StoryVoice.Infrastructure.Health;
using StoryVoice.Infrastructure.Identity;
using StoryVoice.Infrastructure.Insights;
using StoryVoice.Infrastructure.Persistence;
using StoryVoice.Infrastructure.VoiceCatalog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddScoped<IBookService, BookService>();
builder.Services.AddScoped<IBookImportService, BookImportService>();
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = CharacterVoiceProfileLimits.MaximumReferenceAudioBytes + 64 * 1024);
builder.Services.AddStoryVoiceInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ExternalVoiceUsageBackgroundQueue>();
builder.Services.AddSingleton<IExternalVoiceUsageRecorder>(provider =>
    provider.GetRequiredService<ExternalVoiceUsageBackgroundQueue>());
builder.Services.AddHostedService(provider =>
    provider.GetRequiredService<ExternalVoiceUsageBackgroundQueue>());
builder.Services.AddSingleton<ExternalVoiceRequestRateLimiter>();
builder.Services.AddSingleton<IExternalVoiceRequestRateLimiter>(provider =>
    provider.GetRequiredService<ExternalVoiceRequestRateLimiter>());
builder.Services.AddSingleton<ExternalVoicePreAuthenticationRateLimiter>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "StoryVoice.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<StoryVoiceDbContext>()
    .AddSignInManager();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
        options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
    })
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "StoryVoice.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ExternalVoiceAuthenticationHandler>(
        ExternalVoiceAuthenticationDefaults.Scheme,
        _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(StoryVoicePolicies.UserSession, policy =>
    {
        policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme);
        policy.RequireAuthenticatedUser();
    })
    .AddPolicy(StoryVoicePolicies.ExternalVoiceSynthesis, policy =>
    {
        policy.AddAuthenticationSchemes(ExternalVoiceAuthenticationDefaults.Scheme);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(
            ExternalVoiceAuthenticationDefaults.ScopeClaim,
            ExternalVoiceAuthenticationDefaults.SynthesisScope);
    });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = ExternalVoiceEndpoints.WriteRateLimitRejectionAsync;
    options.AddPolicy<string>(ExternalVoiceEndpoints.RateLimitPolicy, httpContext =>
    {
        var consumerKeyId = httpContext.User.FindFirst(
            ExternalVoiceAuthenticationDefaults.ConsumerKeyIdClaim)?.Value;
        if (string.IsNullOrEmpty(consumerKeyId))
        {
            return RateLimitPartition.GetNoLimiter("unauthenticated");
        }

        return httpContext.RequestServices
            .GetRequiredService<ExternalVoiceRequestRateLimiter>()
            .GetPartition(consumerKeyId);
    });
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // 部署鏈是兩跳代理：主機 nginx（https 終端）→ web 容器 nginx → API，
    // 兩層都 append X-Forwarded-For，所以要消化兩組；防偽造靠下面的信任網段
    // 檢查——中介軟體由右往左走，一旦碰到不在信任清單裡的來源就停下，
    // 網際網路客戶端自帶的假 X-Forwarded-For 永遠不會被採用。
    options.ForwardLimit = 2;
    // 只信任本機與私有網段的代理（兩跳都在 docker bridge／loopback 上）。
    // 兩個清單都清空會讓中介軟體信任任何直連客戶端，等於人人可偽造轉送標頭。
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Loopback, 8));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Loopback, 128));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
});
var dataProtectionKeysPath = builder.Configuration["Auth:DataProtectionKeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("StoryVoice")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

var healthChecks = builder.Services.AddHealthChecks()
    .AddDbContextCheck<StoryVoiceDbContext>("postgres", tags: ["ready"]);

if (!builder.Environment.IsEnvironment("Testing"))
{
    var redisConnection = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(redisConnection));
    builder.Services.RemoveAll<ILocalGpuExecutionGate>();
    builder.Services.AddSingleton<ILocalGpuExecutionGate, RedisLocalGpuExecutionGate>();
    healthChecks.AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
}

var app = builder.Build();

app.UseForwardedHeaders();

var configuredPathBase = builder.Configuration["ReverseProxy:PathBase"]?.TrimEnd('/');
if (!string.IsNullOrWhiteSpace(configuredPathBase))
{
    app.Use((context, next) =>
    {
        if (context.Request.Headers.TryGetValue("X-Forwarded-Prefix", out var forwardedPrefix)
            && string.Equals(forwardedPrefix.ToString(), configuredPathBase, StringComparison.Ordinal))
        {
            context.Request.PathBase = new PathString(configuredPathBase);
        }

        return next(context);
    });
}

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseRouting();
// Reject unauthenticated floods before the bearer handler performs a managed
// credential database lookup. Forwarded headers have already resolved the
// trusted client address at this point.
app.UseMiddleware<ExternalVoicePreAuthenticationRateLimitMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ExternalVoiceUsageMiddleware>();
// Endpoint-specific authentication runs during authorization because the regular
// web application keeps its cookie scheme as the default. Rate limiting must run
// afterwards so its partition is the authenticated external consumer, not an
// unauthenticated shared bucket.
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<StoryVoiceDbContext>();
    await database.Database.MigrateAsync();
}

app.MapGet("/", () => Results.Ok(new
{
    name = "StoryVoice",
    tagline = "Turn books into performances.",
    status = "foundation"
}));

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = static async (context, _) =>
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("healthy");
    }
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});

app.MapAccountEndpoints();
app.MapBookEndpoints();
app.MapBookInsightEndpoints();
app.MapNarrationEndpoints();
app.MapLibraryStatusEndpoints();
app.MapSeriesEndpoints();
app.MapSeriesNarrationEndpoints();
app.MapCollectionEndpoints();
app.MapSpeechPlanEndpoints();
app.MapCharacterProfileEndpoints();
app.MapCharacterVoiceProfileEndpoints();
app.MapDeveloperConsoleEndpoints();
var externalVoiceApiOptions = app.Services.GetRequiredService<IOptions<ExternalVoiceApiOptions>>().Value;
if (externalVoiceApiOptions.Enabled)
{
    app.MapExternalVoiceEndpoints();
}
var voiceCatalogOptions = app.Services.GetRequiredService<IOptions<VoiceCatalogOptions>>().Value;
if (voiceCatalogOptions.Enabled)
{
    app.MapPublicVoiceCatalogEndpoints();
}
app.Run();

public partial class Program;

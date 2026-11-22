using FluentValidation;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Infrastructure;
using KatalepsisFT.Infrastructure.Health;
using KatalepsisFT.Persistence;
using KatalepsisFT.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using StackExchange.Redis;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

(bool hasPostgres, bool hasKeyValue) = builder.Services.AddStores(builder.Configuration);
if (!hasPostgres)
{
    throw new InvalidOperationException($"{KatalepsisFT.Infrastructure.DependencyInjection.PostgresUrlKey} is not set; the web service cannot start without its database.");
}

builder.Services.AddPersistence();
builder.Services.AddSingleton<HeartbeatRegistry>();
builder.Services.AddSingleton(provider => new ReadinessProbe(
    provider.GetRequiredService<HeartbeatRegistry>(),
    provider.GetService<NpgsqlDataSource>(),
    provider.GetService<IConnectionMultiplexer>()
));
if (hasKeyValue)
{
    builder.Services.AddHostedService<HeartbeatListener>();
}

// Sign-in: built-in accounts, cookie session.
builder.Services.AddFtIdentityCore().AddSignInManager().AddClaimsPrincipalFactory<FtClaimsPrincipalFactory>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "kft_session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    // This is an API behind a single-page app: answer with a status code, never a redirect to a login page.
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
});

// Everything requires a signed-in user unless an endpoint says otherwise.
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// The keys that protect the session cookie live in Postgres, so either web instance can read a session the other issued.
builder.Services.AddDataProtection().SetApplicationName("KatalepsisFT").PersistKeysToDbContext<FtDbContext>();

builder.Services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
builder.Services.AddScoped<IValidator<ChangePasswordRequest>, ChangePasswordRequestValidator>();
builder.Services.AddControllers();

// Render terminates HTTPS in front of the service; trust its forwarded scheme so the app knows the request was secure.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

WebApplication app = builder.Build();

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseApiGuards();
app.UseAuthorization();

// Liveness: answers as long as the process runs. Render uses this to decide whether an instance may take traffic.
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// Readiness, public: only whether the instance can reach its stores. The detail is at /api/system/status for Admins.
app.MapGet(
        "/health/ready",
        async (ReadinessProbe probe, CancellationToken cancellationToken) =>
        {
            ReadinessReport report = await probe.CheckAsync(cancellationToken);
            return Results.Json(new { ready = report.IsReady }, statusCode: report.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }
    )
    .AllowAnonymous();

app.MapControllers();

app.Run();

public partial class Program;

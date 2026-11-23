using System.Text.Json.Serialization;
using FluentValidation;
using KatalepsisFT.Application;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Live;
using KatalepsisFT.Infrastructure.KeyValue;
using KatalepsisFT.Web.Live;
using KatalepsisFT.Application.Structure;
using KatalepsisFT.Venues;
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
builder.Services.AddScoped<IValidator<SaveAlertRuleRequest>, SaveAlertRuleRequestValidator>();
builder.Services.AddScoped<IValidator<SnoozeRequest>, SnoozeRequestValidator>();
builder.Services.AddScoped<IValidator<KatalepsisFT.Application.Reports.SaveReportScheduleRequest>, KatalepsisFT.Application.Reports.SaveReportScheduleRequestValidator>();
builder.Services.AddScoped<IValidator<NoteRequest>, NoteRequestValidator>();
builder.Services.AddScoped<IValidator<AddConnectionRequest>, AddConnectionRequestValidator>();
builder.Services.AddScoped<IValidator<NewFundRequest>, NewFundRequestValidator>();
builder.Services.AddScoped<IValidator<SaveFundRequest>, SaveFundRequestValidator>();
builder.Services.AddScoped<IValidator<SaveSleeveRequest>, SaveSleeveRequestValidator>();
builder.Services.AddScoped<IValidator<SaveManagerRequest>, SaveManagerRequestValidator>();
builder.Services.AddCredentialVault(builder.Configuration);
builder.Services.AddVenueAdapters();
builder.Services.AddMarketData();
builder.Services.AddApplication();
builder.Services.AddMail();

// Live updates: with Key Value, workers and both web instances announce changes over it; without, this instance tells its own browsers.
// Outgoing request logs print full addresses. Nothing secret is put in an address, and this keeps it that way if one ever is.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddSignalR();
if (hasKeyValue)
{
    builder.Services.AddSingleton<IFundChangeNotifier, KeyValueFundChangeNotifier>();
    builder.Services.AddHostedService<FundChangeRelay>();
}
else
{
    builder.Services.AddSingleton<IFundChangeNotifier, DirectFundChangeNotifier>();
}

builder.Services.AddScoped<KatalepsisFT.Web.Audit.AuditFilter>();
builder.Services.AddControllers(options => options.Filters.AddService<KatalepsisFT.Web.Audit.AuditFilter>()).AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Render terminates HTTPS in front of the service; trust its forwarded scheme so the app knows the request was secure.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Sign-in attempts are counted per caller address as well as per account, so one caller cannot try many accounts quickly.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(
        "sign-in",
        context =>
            context.Connection.RemoteIpAddress is { } caller
                ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(caller.ToString(), _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) })
                // Only an in-process test host has no caller address.
                : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter(string.Empty)
    );
});

WebApplication app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Every answer: no guessing of content types, no display inside another site's frame, no address sent on to other sites.
app.Use(
    (context, next) =>
    {
        IHeaderDictionary headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy = "frame-ancestors 'none'; object-src 'none'; base-uri 'self'";
        return next();
    }
);
// The Angular app: files are public, the data behind them is not.
app.UseDefaultFiles();
// Script and style files carry a hash of their content in their name, so they can be kept for good. The page that
// names them must be asked for every time, or a browser would keep loading a version whose files a release has removed.
StaticFileOptions appFiles = new()
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl = context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public, max-age=31536000, immutable",
};
app.UseStaticFiles(appFiles);
app.UseRateLimiter();
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
app.MapHub<LiveHub>(LiveHub.Path);

// Unknown API paths are a 404; any other unknown path is a page inside the Angular app.
app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("index.html", appFiles).AllowAnonymous();

app.Run();

public partial class Program;

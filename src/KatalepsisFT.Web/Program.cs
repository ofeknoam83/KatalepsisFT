using KatalepsisFT.Infrastructure;
using KatalepsisFT.Infrastructure.Health;
using Npgsql;
using StackExchange.Redis;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

(bool _, bool hasKeyValue) = builder.Services.AddStores(builder.Configuration);
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

WebApplication app = builder.Build();

// Liveness: answers as long as the process runs. Render uses this to decide whether an instance may take traffic.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness: what the instance can reach right now, and when each worker was last heard over pub/sub.
app.MapGet(
    "/health/ready",
    async (ReadinessProbe probe, CancellationToken cancellationToken) =>
    {
        ReadinessReport report = await probe.CheckAsync(cancellationToken);
        return Results.Json(report, statusCode: report.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
);

app.Run();

public partial class Program;

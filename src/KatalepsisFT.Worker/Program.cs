using KatalepsisFT.Application.Connections;
using KatalepsisFT.Infrastructure;
using KatalepsisFT.Persistence;
using KatalepsisFT.Venues;
using KatalepsisFT.Worker;
using KatalepsisFT.Infrastructure.Health;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Each Render worker service is started with its group: --group cefi | defi | core
string? group = builder.Configuration["group"];
if (group is null || !WorkerGroups.All.Contains(group, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Start the worker with --group followed by one of: {string.Join(", ", WorkerGroups.All)}.");
    return 2;
}

(bool hasPostgres, bool hasKeyValue) = builder.Services.AddStores(builder.Configuration);
if (!hasKeyValue)
{
    Console.Error.WriteLine($"{KatalepsisFT.Infrastructure.DependencyInjection.KeyValueUrlKey} is not set; the worker has nothing to connect to.");
    return 2;
}

builder.Services.AddSingleton(new WorkerIdentity(group.ToLowerInvariant()));
builder.Services.AddHostedService<HeartbeatPublisher>();

// The venue workers read their connections from Postgres and write the managers' books back to it.
if (hasPostgres && group.ToLowerInvariant() is WorkerGroups.Cefi or WorkerGroups.Defi)
{
    // One line per SQL statement every minute would bury everything else.
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
    builder.Services.AddPersistence();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddCredentialVault(builder.Configuration);
    builder.Services.AddVenueAdapters();
    builder.Services.AddScoped<VenueAdapterRegistry>();
    builder.Services.AddScoped<ManagerBookWriter>();
    builder.Services.AddScoped<KatalepsisFT.Application.Risk.RiskService>();
    builder.Services.AddScoped<KatalepsisFT.Application.Alerts.AlertEvaluator>();
    builder.Services.AddScoped<KatalepsisFT.Application.Live.IntradayService>();
    builder.Services.AddSingleton<KatalepsisFT.Application.Live.IFundChangeNotifier, KatalepsisFT.Infrastructure.KeyValue.KeyValueFundChangeNotifier>();
    builder.Services.AddScoped<ManagerBookService>();
    builder.Services.AddScoped<ConnectionSyncService>();
    builder.Services.AddHostedService<ConnectionSyncWorker>();
}

// The core worker keeps price history complete, so a newly seen asset gets real risk figures within minutes.
if (hasPostgres && group.ToLowerInvariant() is WorkerGroups.Core)
{
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
    builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    builder.Services.AddPersistence();
    builder.Services.AddMarketData();
    builder.Services.AddScoped<KatalepsisFT.Application.Prices.PriceBackfillService>();
    builder.Services.AddScoped<ManagerBookWriter>();
    builder.Services.AddScoped<KatalepsisFT.Application.Risk.RiskService>();
    builder.Services.AddScoped<KatalepsisFT.Application.Alerts.AlertEvaluator>();
    builder.Services.AddScoped<KatalepsisFT.Application.Live.IntradayService>();
    builder.Services.AddSingleton<KatalepsisFT.Application.Live.IFundChangeNotifier, KatalepsisFT.Infrastructure.KeyValue.KeyValueFundChangeNotifier>();
    builder.Services.AddScoped<ManagerBookService>();
    builder.Services.AddHostedService<PriceHistoryWorker>();
}

await builder.Build().RunAsync();
return 0;

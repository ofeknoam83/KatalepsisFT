using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Demo;
using KatalepsisFT.Application.Risk;
using KatalepsisFT.Application.Prices;
using KatalepsisFT.Infrastructure;
using KatalepsisFT.Infrastructure.MarketData;
using KatalepsisFT.Infrastructure.Health;
using KatalepsisFT.Persistence;
using KatalepsisFT.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;

// One-shot commands, run by hand, by Render's pre-deploy step or by a cron job.
//   check    verifies that Postgres and Key Value answer
//   migrate  brings the database schema up to date, creates the roles and, when the system has no user, the first Admin
//   demo     loads daily prices, brings the demo fund up to the latest day with prices and evaluates its alert rules
//   deploy   migrate, then demo; run by Render before each deploy. A failure of the demo step does not stop the deploy.
const string Usage = "Usage: KatalepsisFT.Jobs check | migrate | demo | deploy";
string command = args.Length > 0 ? args[0] : string.Empty;
if (command is not ("check" or "migrate" or "demo" or "deploy"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
ServiceCollection services = new();
services.AddLogging(logging => logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
(bool hasPostgres, bool _) = services.AddStores(configuration);

if (command == "check")
{
    await using ServiceProvider checkProvider = services.BuildServiceProvider();
    ReadinessProbe probe = new(new HeartbeatRegistry(), checkProvider.GetService<NpgsqlDataSource>(), checkProvider.GetService<IConnectionMultiplexer>());
    ReadinessReport report = await probe.CheckAsync(CancellationToken.None);
    Console.WriteLine($"Postgres: {report.Postgres}");
    Console.WriteLine($"Key Value: {report.KeyValue}");
    return report.Postgres == ReadinessReport.Ok && report.KeyValue == ReadinessReport.Ok ? 0 : 1;
}

if (!hasPostgres)
{
    Console.Error.WriteLine($"{KatalepsisFT.Infrastructure.DependencyInjection.PostgresUrlKey} is not set; there is no database to migrate.");
    return 2;
}

services.AddPersistence();
services.AddFtIdentityCore();
services.AddScoped<InitialAdminSeeder>();
services.AddMarketData();
services.AddScoped<PriceBackfillService>();
services.AddScoped<DemoFundSeeder>();
services.AddScoped<RiskService>();
services.AddScoped<KatalepsisFT.Application.Risk.VenueExposureService>();
services.AddScoped<AlertEvaluator>();
services.AddScoped<KatalepsisFT.Application.Live.IntradayService>();
services.AddSingleton(TimeProvider.System);
// Every service is checked when the job starts, whatever the job: a registration that is missing must stop the
// build's own run of this program, not the release it would otherwise break half way through.
await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
await using AsyncServiceScope scope = provider.CreateAsyncScope();

if (command == "demo")
{
    await RunDemoAsync(scope.ServiceProvider);
    return 0;
}

FtDbContext database = scope.ServiceProvider.GetRequiredService<FtDbContext>();
List<string> pending = (await database.Database.GetPendingMigrationsAsync()).ToList();
await database.Database.MigrateAsync();
Console.WriteLine(pending.Count == 0 ? "Schema already up to date." : $"Applied {pending.Count} migration(s): {string.Join(", ", pending)}");

InitialAdminSeeder seeder = scope.ServiceProvider.GetRequiredService<InitialAdminSeeder>();
await seeder.EnsureRolesAsync();
InitialAdminOutcome outcome = await seeder.EnsureInitialAdminAsync(configuration["INITIAL_ADMIN_EMAIL"], configuration["INITIAL_ADMIN_PASSWORD"], CancellationToken.None);
Console.WriteLine(outcome switch
{
    InitialAdminOutcome.Created => "Created the first Admin account. It must change its password at first sign-in.",
    InitialAdminOutcome.UsersAlreadyExist => "Accounts already exist; no account was created.",
    _ => "No account exists and INITIAL_ADMIN_EMAIL / INITIAL_ADMIN_PASSWORD are not both set; no account was created.",
});

if (command == "deploy")
{
    try
    {
        await RunDemoAsync(scope.ServiceProvider);
    }
    catch (Exception e) when (e is MarketDataUnavailableException or HttpRequestException or TaskCanceledException)
    {
        // The price feed being down must not block a release; the next run catches up.
        Console.WriteLine($"Demo fund not updated: {e.Message}");
    }
}

return 0;

static async Task RunDemoAsync(IServiceProvider services)
{
    DemoFundSeeder seeder = services.GetRequiredService<DemoFundSeeder>();
    await seeder.EnsureAssetsAsync(CancellationToken.None);
    PriceBackfillResult prices = await services.GetRequiredService<PriceBackfillService>().RunAsync(DemoFundDefinition.HistoryStart, CancellationToken.None);
    Console.WriteLine($"Prices: {prices.BarsAdded} daily bars added." + (prices.AssetsWithoutData.Count > 0 ? $" No data for {string.Join(", ", prices.AssetsWithoutData)}." : string.Empty));
    DemoSeedResult demo = await seeder.EnsureFundAsync(CancellationToken.None);
    Console.WriteLine(demo.WaitingFor is not null
        ? $"Demo fund: waiting for {demo.WaitingFor}."
        : $"Demo fund: {(demo.FundCreated ? "created, " : string.Empty)}{demo.DaysAdded} day(s) and {demo.PositionRowsAdded} position rows added.");
    int pruned = await services.GetRequiredService<KatalepsisFT.Application.Live.IntradayService>().PruneAsync(CancellationToken.None);
    if (pruned > 0)
    {
        Console.WriteLine($"Intraday samples: {pruned} older than 90 days removed.");
    }

    if (demo.WaitingFor is null)
    {
        FtDbContext db = services.GetRequiredService<FtDbContext>();
        foreach (int fundId in await db.Funds.Select(f => f.Id).ToListAsync())
        {
            EvaluationResultDto alerts = await services.GetRequiredService<AlertEvaluator>().EvaluateAsync(fundId, CancellationToken.None);
            Console.WriteLine($"Alerts: {alerts.Raised} raised, {alerts.Recovered} recovered, {alerts.Active} active.");
        }
    }
}

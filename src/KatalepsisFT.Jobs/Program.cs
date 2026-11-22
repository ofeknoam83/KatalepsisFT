using KatalepsisFT.Infrastructure;
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
const string Usage = "Usage: KatalepsisFT.Jobs check | migrate";
string command = args.Length > 0 ? args[0] : string.Empty;
if (command is not ("check" or "migrate"))
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
await using ServiceProvider provider = services.BuildServiceProvider();
await using AsyncServiceScope scope = provider.CreateAsyncScope();

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
return 0;

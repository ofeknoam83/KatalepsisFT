using KatalepsisFT.Infrastructure;
using KatalepsisFT.Infrastructure.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;

// One-shot commands, run by hand or by a Render cron job. Phase 0 has one: "check".
string command = args.Length > 0 ? args[0] : string.Empty;
if (command != "check")
{
    Console.Error.WriteLine("Usage: KatalepsisFT.Jobs check   (verifies that Postgres and Key Value answer)");
    return 2;
}

IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
ServiceCollection services = new();
services.AddStores(configuration);
await using ServiceProvider provider = services.BuildServiceProvider();

ReadinessProbe probe = new(new HeartbeatRegistry(), provider.GetService<NpgsqlDataSource>(), provider.GetService<IConnectionMultiplexer>());
ReadinessReport report = await probe.CheckAsync(CancellationToken.None);
Console.WriteLine($"Postgres: {report.Postgres}");
Console.WriteLine($"Key Value: {report.KeyValue}");

bool bothAnswer = report.Postgres == ReadinessReport.Ok && report.KeyValue == ReadinessReport.Ok;
return bothAnswer ? 0 : 1;

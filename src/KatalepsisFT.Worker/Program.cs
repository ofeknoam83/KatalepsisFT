using KatalepsisFT.Infrastructure;
using KatalepsisFT.Infrastructure.Health;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Each Render worker service is started with its group: --group cefi | defi | core
string? group = builder.Configuration["group"];
if (group is null || !WorkerGroups.All.Contains(group, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Start the worker with --group followed by one of: {string.Join(", ", WorkerGroups.All)}.");
    return 2;
}

(bool _, bool hasKeyValue) = builder.Services.AddStores(builder.Configuration);
if (!hasKeyValue)
{
    Console.Error.WriteLine($"{DependencyInjection.KeyValueUrlKey} is not set; the worker has nothing to connect to.");
    return 2;
}

builder.Services.AddSingleton(new WorkerIdentity(group.ToLowerInvariant()));
builder.Services.AddHostedService<HeartbeatPublisher>();

await builder.Build().RunAsync();
return 0;

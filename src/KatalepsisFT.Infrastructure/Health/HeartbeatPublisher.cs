using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace KatalepsisFT.Infrastructure.Health;

/// <summary>Publishes this worker's group name every few seconds and keeps a key with a short expiry as a second sign of life.</summary>
public sealed class HeartbeatPublisher(IConnectionMultiplexer keyValue, WorkerIdentity identity, ILogger<HeartbeatPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker {Group} started", identity.Group);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IDatabase database = keyValue.GetDatabase();
                await database.StringSetAsync($"{HeartbeatRegistry.Channel}:{identity.Group}", DateTimeOffset.UtcNow.ToString("O"), KeyLifetime);
                await keyValue.GetSubscriber().PublishAsync(RedisChannel.Literal(HeartbeatRegistry.Channel), identity.Group);
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                logger.LogWarning(e, "Worker {Group} could not publish its heartbeat", identity.Group);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public sealed record WorkerIdentity(string Group);

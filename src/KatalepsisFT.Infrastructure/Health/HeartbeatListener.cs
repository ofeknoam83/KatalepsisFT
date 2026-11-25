using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace KatalepsisFT.Infrastructure.Health;

/// <summary>Subscribes the web service to worker heartbeats. This is also the proof that pub/sub works on the Key Value instance.</summary>
public sealed class HeartbeatListener(IConnectionMultiplexer keyValue, HeartbeatRegistry registry, ILogger<HeartbeatListener> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            ChannelMessageQueue queue = await keyValue.GetSubscriber().SubscribeAsync(RedisChannel.Literal(HeartbeatRegistry.Channel));
            queue.OnMessage(message =>
            {
                string? group = message.Message;
                if (!string.IsNullOrEmpty(group))
                {
                    registry.Record(group, DateTimeOffset.UtcNow);
                }
            });
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            // The multiplexer keeps retrying and restores the subscription when the connection returns.
            logger.LogWarning(e, "Could not subscribe to worker heartbeats yet");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

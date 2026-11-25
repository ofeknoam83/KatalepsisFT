using KatalepsisFT.Application.Live;
using KatalepsisFT.Infrastructure.KeyValue;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace KatalepsisFT.Web.Live;

/// <summary>
/// The one live connection of a browser. It says which fund it is looking at and is told when that fund's figures
/// change; it then loads them through the ordinary API, so nothing but "fund N changed" ever travels on this socket.
/// </summary>
public sealed class LiveHub : Hub
{
    public const string Path = "/hubs/live";
    public const string FundChanged = "fundChanged";

    public static string Group(int fundId) => $"fund:{fundId}";

    public Task WatchFund(int fundId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(fundId));

    public Task UnwatchFund(int fundId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(fundId));
}

/// <summary>Pushes straight to this instance's browsers. Used when there is no Key Value to carry the message between instances.</summary>
public sealed class DirectFundChangeNotifier(IHubContext<LiveHub> hub) : IFundChangeNotifier
{
    public Task NotifyAsync(int fundId, CancellationToken cancellationToken) => hub.Clients.Group(LiveHub.Group(fundId)).SendAsync(LiveHub.FundChanged, fundId, cancellationToken);
}

/// <summary>
/// Each web instance listens by itself and tells its own browsers, so nothing has to be passed between instances and
/// no browser needs to stay on the instance it first reached.
/// </summary>
public sealed class FundChangeRelay(IConnectionMultiplexer keyValue, IHubContext<LiveHub> hub, ILogger<FundChangeRelay> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            ChannelMessageQueue queue = await keyValue.GetSubscriber().SubscribeAsync(RedisChannel.Literal(KeyValueFundChangeNotifier.Channel));
            queue.OnMessage(async message =>
            {
                if (message.Message.TryParse(out int fundId))
                {
                    await hub.Clients.Group(LiveHub.Group(fundId)).SendAsync(LiveHub.FundChanged, fundId);
                }
            });
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            // The multiplexer keeps retrying and restores the subscription when the connection returns.
            logger.LogWarning(e, "Could not subscribe to fund changes yet");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

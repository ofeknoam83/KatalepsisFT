using KatalepsisFT.Application.Live;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace KatalepsisFT.Infrastructure.KeyValue;

/// <summary>
/// "This fund's figures changed", sent over Key Value pub/sub so every web instance hears it from every worker. The
/// message carries only the fund's id; whoever cares reads the figures from Postgres.
/// </summary>
public sealed class KeyValueFundChangeNotifier(IConnectionMultiplexer keyValue, ILogger<KeyValueFundChangeNotifier> logger) : IFundChangeNotifier
{
    public const string Channel = "ft:fund-changed";

    public async Task NotifyAsync(int fundId, CancellationToken cancellationToken)
    {
        try
        {
            await keyValue.GetSubscriber().PublishAsync(RedisChannel.Literal(Channel), fundId);
        }
        catch (RedisException e)
        {
            // Screens fall back to refreshing on a timer; the figures themselves are already saved.
            logger.LogWarning(e, "Could not announce a change to fund {FundId}", fundId);
        }
    }
}

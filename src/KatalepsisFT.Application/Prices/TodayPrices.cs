using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Application.Prices;

/// <summary>
/// What assets cost right now, for figures that compare against today. Daily closes only reach yesterday, so without
/// this a benchmark would stand still today while the fund it is compared with moves. Prices are kept for a minute,
/// and a source that fails or is slow simply gives nothing: the comparison then ends at yesterday's close, as before.
/// </summary>
public sealed class TodayPrices(IServiceProvider services, TimeProvider clock)
{
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, (decimal Price, DateTimeOffset At)> _kept = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, decimal>> GetAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        List<string> stale = symbols.Where(s => !_kept.TryGetValue(s, out (decimal Price, DateTimeOffset At) kept) || now - kept.At > KeptFor).ToList();
        if (stale.Count > 0 && services.GetService<ILivePriceSource>() is { } source)
        {
            try
            {
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(Patience);
                foreach ((string symbol, decimal price) in await source.GetPricesAsync(stale, limit.Token))
                {
                    if (price > 0m)
                    {
                        _kept[symbol] = (price, now);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // No live price this time. Whatever is still fresh enough below is used; the rest is left out.
            }
        }

        return symbols.Where(s => _kept.TryGetValue(s, out (decimal Price, DateTimeOffset At) kept) && now - kept.At <= KeptFor * 5).ToDictionary(s => s, s => _kept[s].Price, StringComparer.OrdinalIgnoreCase);
    }
}

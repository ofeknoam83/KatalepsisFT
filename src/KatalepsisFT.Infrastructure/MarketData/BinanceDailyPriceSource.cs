using KatalepsisFT.Application.Prices;

namespace KatalepsisFT.Infrastructure.MarketData;

/// <summary>Daily bars from Binance's public market data. USDT pairs are treated as USD, as in Katalepsis.</summary>
public sealed class BinanceDailyPriceSource(BinancePublicClient client) : IDailyPriceSource
{
    public string Name => "binance";

    public async Task<IReadOnlyList<DailyBar>?> GetDailyBarsAsync(string assetSymbol, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        DailySeries? series = await client.GetDailyRangeAsync(
            assetSymbol,
            from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            cancellationToken
        );
        if (series is null)
        {
            return null;
        }

        List<DailyBar> bars = new(series.Days.Count);
        for (int i = 0; i < series.Days.Count; i++)
        {
            bars.Add(new DailyBar(DateOnly.FromDateTime(series.Days[i]), series.Closes[i], Math.Round(series.QuoteVolumes[i], 2)));
        }

        return bars;
    }
}

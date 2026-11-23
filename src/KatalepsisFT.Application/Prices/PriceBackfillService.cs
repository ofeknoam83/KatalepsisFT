using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Prices;

public sealed record PriceBackfillResult(int BarsAdded, IReadOnlyList<string> AssetsWithoutData);

/// <summary>Brings the daily price history of every crypto asset up to yesterday. Safe to run repeatedly.</summary>
public sealed class PriceBackfillService(IFtDbContext database, IDailyPriceSource source, TimeProvider time)
{
    /// <param name="skip">Assets not to ask about this time, such as ones the source was recently found not to list.</param>
    public async Task<PriceBackfillResult> RunAsync(DateOnly historyStart, CancellationToken cancellationToken, IReadOnlySet<string>? skip = null)
    {
        // Today's bar is not complete yet.
        DateOnly yesterday = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1);
        List<Asset> assets = await database.Assets.Where(a => a.AssetClass == AssetClass.Crypto).OrderBy(a => a.Symbol).ToListAsync(cancellationToken);
        Dictionary<int, DateOnly> lastDates = await database
            .PriceBars.GroupBy(b => b.AssetId)
            .Select(g => new { AssetId = g.Key, Last = g.Max(b => b.Date) })
            .ToDictionaryAsync(x => x.AssetId, x => x.Last, cancellationToken);

        int added = 0;
        List<string> missing = [];
        foreach (Asset asset in assets)
        {
            DateOnly from = lastDates.TryGetValue(asset.Id, out DateOnly last) ? last.AddDays(1) : historyStart;
            if (from > yesterday || skip?.Contains(asset.Symbol) == true)
            {
                continue;
            }

            IReadOnlyList<DailyBar>? bars = await source.GetDailyBarsAsync(asset.Symbol, from, yesterday, cancellationToken);
            if (bars is null || bars.Count == 0)
            {
                if (!lastDates.ContainsKey(asset.Id))
                {
                    missing.Add(asset.Symbol);
                }

                continue;
            }

            foreach (DailyBar bar in bars.Where(b => b.Date >= from && b.Date <= yesterday && b.Close > 0m))
            {
                database.PriceBars.Add(
                    new PriceBar
                    {
                        AssetId = asset.Id,
                        Date = bar.Date,
                        Close = bar.Close,
                        VolumeUsd = bar.VolumeUsd,
                        Source = source.Name,
                    }
                );
                added++;
            }

            await database.SaveChangesAsync(cancellationToken);
        }

        return new PriceBackfillResult(added, missing);
    }
}

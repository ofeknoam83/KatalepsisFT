using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Prices;

public sealed record PriceBackfillResult(int BarsAdded, IReadOnlyList<string> AssetsWithoutData);

/// <summary>Brings the daily price history of every asset that has a price source up to yesterday: crypto and equities. Safe to run repeatedly.</summary>
public sealed class PriceBackfillService(IFtDbContext database, IEnumerable<IDailyPriceSource> sources, TimeProvider time)
{
    private readonly Dictionary<AssetClass, IDailyPriceSource> _sources = sources.ToDictionary(s => s.AssetClass);

    /// <param name="skip">Assets not to ask about this time, such as ones the source was recently found not to list.</param>
    public async Task<PriceBackfillResult> RunAsync(DateOnly historyStart, CancellationToken cancellationToken, IReadOnlySet<string>? skip = null)
    {
        // Today's bar is not complete yet.
        DateOnly yesterday = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1);
        AssetClass[] priced = _sources.Keys.ToArray();
        List<Asset> assets = await database.Assets.Where(a => priced.Contains(a.AssetClass)).OrderBy(a => a.Symbol).ToListAsync(cancellationToken);
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

            IDailyPriceSource source = _sources[asset.AssetClass];
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

    /// <summary>
    /// Loads the history of one asset now, creating the asset if this is the first time it is seen. Used when someone
    /// enters a position in it and should not have to wait for the next scheduled run.
    /// </summary>
    /// <returns>True when the asset has price history afterwards.</returns>
    public async Task<bool> EnsureAsync(string symbol, AssetClass assetClass, DateOnly historyStart, CancellationToken cancellationToken)
    {
        Asset? asset = await database.Assets.FirstOrDefaultAsync(a => a.Symbol == symbol, cancellationToken);
        if (asset is not null && await database.PriceBars.AnyAsync(b => b.AssetId == asset.Id, cancellationToken))
        {
            return true;
        }

        if (!_sources.TryGetValue(asset?.AssetClass ?? assetClass, out IDailyPriceSource? source))
        {
            return false;
        }

        DateOnly yesterday = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1);
        IReadOnlyList<DailyBar>? bars = await source.GetDailyBarsAsync(symbol, historyStart, yesterday, cancellationToken);
        List<DailyBar> usable = (bars ?? []).Where(b => b.Date >= historyStart && b.Date <= yesterday && b.Close > 0m).ToList();
        if (usable.Count == 0)
        {
            return false;
        }

        asset ??= database.Assets.Add(new Asset { Symbol = symbol, Name = symbol, AssetClass = assetClass }).Entity;
        foreach (DailyBar bar in usable)
        {
            database.PriceBars.Add(new PriceBar { Asset = asset, Date = bar.Date, Close = bar.Close, VolumeUsd = bar.VolumeUsd, Source = source.Name });
        }

        await database.SaveChangesAsync(cancellationToken);
        return true;
    }
}

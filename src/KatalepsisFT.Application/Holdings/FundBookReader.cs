using KatalepsisFT.Analytics.Holdings;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Holdings;

/// <summary>Everything the holdings screens need about one fund on its latest valued day.</summary>
public sealed record FundBook(Fund Fund, DateOnly AsOf, IReadOnlyList<Sleeve> Sleeves, IReadOnlyList<ManagerAccount> Managers, IReadOnlyList<HoldingRow> Rows)
{
    public FundRefDto FundRef => new(Fund.Id, Fund.Name, Fund.IsDemo, Fund.Benchmark?.Name);

    public static SleeveRefDto Ref(Sleeve sleeve) => new(sleeve.Id, sleeve.Name, sleeve.MandateType);

    public static ManagerRefDto Ref(ManagerAccount manager) => new(manager.Id, manager.Name, manager.SleeveId);

    /// <summary>The rows that count towards exposure under a filter. Cash rows always pass, so NAV stays whole.</summary>
    public static IEnumerable<HoldingRow> Apply(IEnumerable<HoldingRow> rows, InstrumentFilter filter) =>
        filter switch
        {
            InstrumentFilter.Spot => rows.Where(r => r.Type is InstrumentType.Spot),
            InstrumentFilter.Perp => rows.Where(r => r.Type is InstrumentType.Perp),
            InstrumentFilter.Option => rows.Where(r => r.Type is InstrumentType.Option),
            InstrumentFilter.TradFi => rows.Where(r => r.Type is InstrumentType.Equity or InstrumentType.Future),
            _ => rows,
        };

    /// <summary>NAV from every row; long and short only from the rows the filter lets through.</summary>
    public static TotalsDto Totals(IReadOnlyCollection<HoldingRow> rows, InstrumentFilter filter)
    {
        BookTotals all = HoldingsAggregator.Totals(rows);
        if (filter == InstrumentFilter.All)
        {
            return TotalsDto.From(all);
        }

        BookTotals filtered = HoldingsAggregator.Totals(Apply(rows, filter));
        return new TotalsDto(all.NavUsd, filtered.LongUsd, filtered.ShortUsd, filtered.GrossUsd, filtered.NetUsd, all.CashUsd);
    }
}

public sealed class FundBookReader(IFtDbContext database)
{
    public async Task<FundBook?> LoadAsync(int fundId, CancellationToken cancellationToken)
    {
        Fund? fund = await database.Funds.AsNoTracking().Include(f => f.Benchmark).FirstOrDefaultAsync(f => f.Id == fundId, cancellationToken);
        if (fund is null)
        {
            return null;
        }

        List<Sleeve> sleeves = await database.Sleeves.AsNoTracking().Include(s => s.Limits).Where(s => s.FundId == fundId).OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync(cancellationToken);
        List<ManagerAccount> managers = await database.ManagerAccounts.AsNoTracking().Where(m => m.Sleeve.FundId == fundId).OrderBy(m => m.Id).ToListAsync(cancellationToken);

        // The latest day on which every manager with any history has been valued; a fund never mixes days.
        DateOnly? asOf = await database
            .NavSnapshots.Where(n => n.ManagerAccount.Sleeve.FundId == fundId)
            .GroupBy(n => n.ManagerAccountId)
            .Select(g => (DateOnly?)g.Max(n => n.Date))
            .MinAsync(cancellationToken);
        if (asOf is null)
        {
            // Nothing has been read yet: an empty book as of today, so date arithmetic downstream stays in range.
            return new FundBook(fund, DateOnly.FromDateTime(DateTime.UtcNow), sleeves, managers, []);
        }

        Dictionary<int, Sleeve> sleeveById = sleeves.ToDictionary(s => s.Id);
        Dictionary<int, ManagerAccount> managerById = managers.ToDictionary(m => m.Id);
        var raw = await database
            .PositionSnapshots.AsNoTracking()
            .Where(p => p.Date == asOf && p.ManagerAccount.Sleeve.FundId == fundId)
            .Select(p => new
            {
                p.ManagerAccountId,
                Asset = p.Instrument.Asset.Symbol,
                p.Instrument.Type,
                Instrument = p.Instrument.Symbol,
                p.Quantity,
                p.EntryPrice,
                p.MarkPrice,
                p.MarketValueUsd,
                p.ExposureUsd,
                p.UnrealizedPnlUsd,
            })
            .ToListAsync(cancellationToken);

        List<HoldingRow> rows = raw.Select(p =>
            {
                ManagerAccount manager = managerById[p.ManagerAccountId];
                Sleeve sleeve = sleeveById[manager.SleeveId];
                return new HoldingRow(sleeve.Id, sleeve.Name, manager.Id, manager.Name, p.Asset, p.Type, p.Instrument, p.Quantity, p.EntryPrice, p.MarkPrice, p.MarketValueUsd, p.ExposureUsd, p.UnrealizedPnlUsd);
            })
            .ToList();
        return new FundBook(fund, asOf.Value, sleeves, managers, rows);
    }

    /// <summary>Price, changes and average traded volume for the given assets as of a day.</summary>
    public async Task<IReadOnlyDictionary<string, AssetPriceDto>> LoadPricesAsync(IReadOnlyCollection<string> symbols, DateOnly asOf, CancellationToken cancellationToken)
    {
        const int VolumeWindowDays = 30;
        DateOnly yearStart = new DateOnly(asOf.Year, 1, 1).AddDays(-1);
        DateOnly from = yearStart < asOf.AddDays(-VolumeWindowDays) ? yearStart : asOf.AddDays(-VolumeWindowDays);
        var assets = await database.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).Select(a => new { a.Id, a.Symbol, a.Name }).ToListAsync(cancellationToken);
        int[] ids = assets.Select(a => a.Id).ToArray();
        var bars = await database
            .PriceBars.AsNoTracking()
            .Where(b => ids.Contains(b.AssetId) && b.Date >= from && b.Date <= asOf)
            .Select(b => new { b.AssetId, b.Date, b.Close, b.VolumeUsd })
            .ToListAsync(cancellationToken);
        var byAsset = bars.GroupBy(b => b.AssetId).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());

        Dictionary<string, AssetPriceDto> result = new(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            if (!byAsset.TryGetValue(asset.Id, out var series) || series.Count == 0)
            {
                result[asset.Symbol] = new AssetPriceDto(asset.Symbol, asset.Name, null, null, null, null, null);
                continue;
            }

            decimal last = series[^1].Close;
            decimal? Change(DateOnly reference)
            {
                // The close on the reference day, or the last one before it.
                var earlier = series.LastOrDefault(b => b.Date <= reference);
                return earlier is null || earlier.Close <= 0m ? null : Math.Round(last / earlier.Close - 1m, 6);
            }

            var window = series.Where(b => b.Date > asOf.AddDays(-VolumeWindowDays)).ToList();
            result[asset.Symbol] = new AssetPriceDto(
                asset.Symbol,
                asset.Name,
                last,
                Change(series[^1].Date.AddDays(-1)),
                Change(series[^1].Date.AddDays(-30)),
                Change(yearStart),
                window.Count > 0 ? Math.Round(window.Average(b => b.VolumeUsd), 2) : null
            );
        }

        return result;
    }
}

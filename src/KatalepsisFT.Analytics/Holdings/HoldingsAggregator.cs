using KatalepsisFT.Analytics.Valuation;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Analytics.Holdings;

/// <summary>NAV and exposure of a set of positions. Cash counts in NAV and never in exposure.</summary>
public sealed record BookTotals(decimal NavUsd, decimal LongUsd, decimal ShortUsd, decimal CashUsd)
{
    public decimal GrossUsd => LongUsd - ShortUsd;
    public decimal NetUsd => LongUsd + ShortUsd;
}

public sealed record AssetExposure(
    string Asset,
    decimal LongUsd,
    decimal ShortUsd,
    IReadOnlyDictionary<InstrumentType, decimal> NetByType,
    IReadOnlyDictionary<int, decimal> NetByManager,
    IReadOnlyList<int> SleeveIds
)
{
    public decimal GrossUsd => LongUsd - ShortUsd;
    public decimal NetUsd => LongUsd + ShortUsd;

    /// <summary>Some managers are long and others short: the gross is larger than what the fund is actually exposed to.</summary>
    public bool HasOffsettingManagers => NetByManager.Values.Any(v => v > 0m) && NetByManager.Values.Any(v => v < 0m);
}

public sealed record TypeExposure(InstrumentType Type, decimal LongUsd, decimal ShortUsd)
{
    public decimal GrossUsd => LongUsd - ShortUsd;
    public decimal NetUsd => LongUsd + ShortUsd;
}

/// <param name="Share">Exposure both managers have in assets they share, as a fraction of the smaller book's gross.</param>
/// <param name="OppositeSides">Number of shared assets on which the two are on opposite sides.</param>
public sealed record ManagerOverlap(int ManagerA, int ManagerB, decimal Share, int OppositeSides);

/// <summary>
/// Pure aggregation of valued positions. Long and short are summed position by position, so a manager who is long spot
/// and short the perp on the same asset shows both legs in gross and nothing in net.
/// </summary>
public static class HoldingsAggregator
{
    public static BookTotals Totals(IEnumerable<HoldingRow> rows)
    {
        decimal nav = 0m, cash = 0m;
        List<decimal> exposures = [];
        foreach (HoldingRow row in rows)
        {
            nav += row.MarketValueUsd;
            if (row.Type == InstrumentType.Cash)
            {
                cash += row.MarketValueUsd;
            }
            else
            {
                exposures.Add(row.ExposureUsd);
            }
        }

        ExposureSummary summary = ExposureSummary.Of(exposures);
        return new BookTotals(nav, summary.LongUsd, summary.ShortUsd, cash);
    }

    /// <summary>One line per asset, largest gross first.</summary>
    public static IReadOnlyList<AssetExposure> ByAsset(IEnumerable<HoldingRow> rows) =>
        rows.Where(r => r.Type != InstrumentType.Cash)
            .GroupBy(r => r.Asset)
            .Select(group =>
            {
                ExposureSummary summary = ExposureSummary.Of(group.Select(r => r.ExposureUsd));
                return new AssetExposure(
                    group.Key,
                    summary.LongUsd,
                    summary.ShortUsd,
                    group.GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Sum(r => r.ExposureUsd)),
                    group.GroupBy(r => r.ManagerId).ToDictionary(g => g.Key, g => g.Sum(r => r.ExposureUsd)),
                    group.Select(r => r.SleeveId).Distinct().Order().ToList()
                );
            })
            .OrderByDescending(a => a.GrossUsd)
            .ThenBy(a => a.Asset, StringComparer.Ordinal)
            .ToList();

    /// <summary>One line per instrument type that is held, in the order of the enum; cash is left out.</summary>
    public static IReadOnlyList<TypeExposure> ByType(IEnumerable<HoldingRow> rows) =>
        rows.Where(r => r.Type != InstrumentType.Cash)
            .GroupBy(r => r.Type)
            .OrderBy(g => g.Key)
            .Select(group =>
            {
                ExposureSummary summary = ExposureSummary.Of(group.Select(r => r.ExposureUsd));
                return new TypeExposure(group.Key, summary.LongUsd, summary.ShortUsd);
            })
            .ToList();

    /// <summary>How much every pair of managers shares, for managers that hold anything besides cash.</summary>
    public static IReadOnlyList<ManagerOverlap> Overlaps(IEnumerable<HoldingRow> rows)
    {
        // Net exposure per manager and asset: a manager hedged on an asset is not "in" it.
        Dictionary<int, Dictionary<string, decimal>> books = rows.Where(r => r.Type != InstrumentType.Cash)
            .GroupBy(r => r.ManagerId)
            .ToDictionary(g => g.Key, g => g.GroupBy(r => r.Asset).ToDictionary(a => a.Key, a => a.Sum(r => r.ExposureUsd)));
        int[] managers = books.Keys.Order().ToArray();
        List<ManagerOverlap> result = [];
        for (int i = 0; i < managers.Length; i++)
        {
            for (int j = i + 1; j < managers.Length; j++)
            {
                Dictionary<string, decimal> a = books[managers[i]], b = books[managers[j]];
                decimal shared = 0m;
                int opposite = 0;
                foreach ((string asset, decimal exposureA) in a)
                {
                    if (!b.TryGetValue(asset, out decimal exposureB) || exposureA == 0m || exposureB == 0m)
                    {
                        continue;
                    }

                    shared += Math.Min(Math.Abs(exposureA), Math.Abs(exposureB));
                    if (Math.Sign(exposureA) != Math.Sign(exposureB))
                    {
                        opposite++;
                    }
                }

                decimal smaller = Math.Min(a.Values.Sum(Math.Abs), b.Values.Sum(Math.Abs));
                result.Add(new ManagerOverlap(managers[i], managers[j], smaller > 0m ? Math.Round(shared / smaller, 4) : 0m, opposite));
            }
        }

        return result;
    }
}

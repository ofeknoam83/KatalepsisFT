using KatalepsisFT.Analytics.Valuation;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Application.Demo;

/// <summary>One held line of a demo book. Quantity is signed; for perps the entry price is the price the day the book started.</summary>
public sealed record DemoLine(string Asset, InstrumentType Type, decimal Quantity, decimal EntryPrice);

public sealed record DemoValuedLine(DemoLine Line, decimal MarkPrice, PositionValue Value);

/// <summary>Pure arithmetic of the demo books: what a manager holds from day one, and what it is worth on a given day.</summary>
public static class DemoBookBuilder
{
    /// <summary>
    /// The book a manager starts with. Spot is paid for out of the starting capital and the rest stays in cash; perps cost
    /// nothing to open. <paramref name="crowdedGrossQuantity"/> is the fund-wide quantity of the crowded asset to share out.
    /// </summary>
    public static IReadOnlyList<DemoLine> Start(DemoManager manager, IReadOnlyDictionary<string, decimal> inceptionPrices, decimal crowdedGrossQuantity)
    {
        List<DemoLine> lines = [];
        foreach (DemoHolding holding in manager.Holdings)
        {
            decimal price = Price(inceptionPrices, holding.Asset);
            lines.Add(new DemoLine(holding.Asset, holding.Type, Math.Round(holding.Weight * manager.CapitalUsd / price, 8), price));
        }

        decimal totalShare = DemoFundDefinition.TotalCrowdedShare;
        foreach (DemoCrowdedHolding crowded in manager.Crowded)
        {
            decimal price = Price(inceptionPrices, DemoFundDefinition.CrowdedAsset);
            lines.Add(new DemoLine(DemoFundDefinition.CrowdedAsset, crowded.Type, Math.Round(crowded.Share / totalShare * crowdedGrossQuantity, 8), price));
        }

        decimal spotCost = lines.Where(l => l.Type == InstrumentType.Spot).Sum(l => l.Quantity * l.EntryPrice);
        decimal cash = Math.Round(manager.CapitalUsd - spotCost, 2);
        if (cash < 0m)
        {
            throw new InvalidOperationException($"{manager.Name} would start with negative cash ({cash:N0}): its spot holdings cost more than its capital.");
        }

        lines.Add(new DemoLine(DemoFundDefinition.CashAsset, InstrumentType.Cash, cash, 1m));
        return lines;
    }

    public static IReadOnlyList<DemoValuedLine> Value(IReadOnlyList<DemoLine> lines, IReadOnlyDictionary<string, decimal> prices)
    {
        List<DemoValuedLine> valued = new(lines.Count);
        foreach (DemoLine line in lines)
        {
            decimal mark = line.Type == InstrumentType.Cash ? 1m : Price(prices, line.Asset);
            PositionValue value = PositionValuer.Value(new PositionInput(line.Type, line.Quantity, mark, EntryPrice: line.EntryPrice));
            valued.Add(new DemoValuedLine(line, mark, value));
        }

        return valued;
    }

    private static decimal Price(IReadOnlyDictionary<string, decimal> prices, string asset) =>
        prices.TryGetValue(asset, out decimal price) && price > 0m ? price : throw new InvalidOperationException($"No price for {asset}.");
}

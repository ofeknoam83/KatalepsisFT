namespace KatalepsisFT.Analytics.Risk;

/// <param name="ValueAtRiskUsd">Loss that was exceeded on only (1 − confidence) of the days; positive.</param>
/// <param name="ExpectedShortfallUsd">Average loss on those worst days; positive.</param>
public sealed record VarResult(decimal ValueAtRiskUsd, decimal ExpectedShortfallUsd, int Days);

/// <summary>
/// Historical simulation: today's positions are held through every day of the look-back, and the losses that would have
/// resulted are ranked. Says nothing about days worse than any in the look-back.
/// </summary>
public static class HistoricalVar
{
    /// <summary>The profit or loss today's exposures would have made on each past day.</summary>
    /// <param name="exposures">Signed exposure per asset.</param>
    /// <param name="returnsByAsset">Daily returns per asset, all of the same length and covering the same days.</param>
    public static decimal[] HypotheticalPnl(IReadOnlyDictionary<string, decimal> exposures, IReadOnlyDictionary<string, double[]> returnsByAsset)
    {
        int days = returnsByAsset.Count == 0 ? 0 : returnsByAsset.Values.Min(r => r.Length);
        decimal[] pnl = new decimal[days];
        foreach ((string asset, decimal exposure) in exposures)
        {
            if (!returnsByAsset.TryGetValue(asset, out double[]? returns))
            {
                continue;
            }

            for (int t = 0; t < days; t++)
            {
                pnl[t] += exposure * (decimal)returns[t];
            }
        }

        return pnl;
    }

    public static VarResult Compute(IReadOnlyList<decimal> pnl, double confidence = 0.95)
    {
        if (pnl.Count == 0)
        {
            return new VarResult(0m, 0m, 0);
        }

        decimal[] sorted = pnl.Order().ToArray();
        // The worst (1 − confidence) share of the days, at least one.
        int tail = Math.Max(1, (int)Math.Floor(sorted.Length * (1.0 - confidence)));
        decimal valueAtRisk = Math.Max(0m, -sorted[tail - 1]);
        decimal shortfall = Math.Max(0m, -sorted.Take(tail).Average());
        return new VarResult(Math.Round(valueAtRisk, 2), Math.Round(shortfall, 2), sorted.Length);
    }
}

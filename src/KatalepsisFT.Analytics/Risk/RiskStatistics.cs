using KatalepsisFT.Analytics.Statistics;

namespace KatalepsisFT.Analytics.Risk;

public static class RiskStatistics
{
    /// <summary>Annualised volatility of the last <paramref name="window"/> returns; null when there are fewer.</summary>
    public static double? Volatility(IReadOnlyList<double> returns, int window)
    {
        if (window < 2 || returns.Count < window)
        {
            return null;
        }

        return Indicators.StdDev(returns.Skip(returns.Count - window).ToArray()) * Math.Sqrt(Indicators.DaysPerYear);
    }

    /// <summary>Annualised volatility over each trailing window; element i covers returns [i, i + window).</summary>
    public static double[] RollingVolatility(IReadOnlyList<double> returns, int window)
    {
        if (window < 2 || returns.Count < window)
        {
            return [];
        }

        double[] result = new double[returns.Count - window + 1];
        double[] buffer = new double[window];
        for (int i = 0; i < result.Length; i++)
        {
            for (int k = 0; k < window; k++)
            {
                buffer[k] = returns[i + k];
            }

            result[i] = Indicators.StdDev(buffer) * Math.Sqrt(Indicators.DaysPerYear);
        }

        return result;
    }

    /// <summary>Correlation of two return series over the same days; null when either does not move.</summary>
    public static double? Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        int n = Math.Min(a.Count, b.Count);
        if (n < 2)
        {
            return null;
        }

        double meanA = a.Take(n).Average(), meanB = b.Take(n).Average();
        double covariance = 0, varianceA = 0, varianceB = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - meanA, db = b[i] - meanB;
            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }

        const double Flat = 1e-18;
        return varianceA > Flat && varianceB > Flat ? covariance / Math.Sqrt(varianceA * varianceB) : null;
    }

    /// <summary>Beta of <paramref name="asset"/> to <paramref name="reference"/>; null when the reference does not move.</summary>
    public static double? Beta(IReadOnlyList<double> asset, IReadOnlyList<double> reference)
    {
        int n = Math.Min(asset.Count, reference.Count);
        if (n < 2)
        {
            return null;
        }

        double meanA = asset.Take(n).Average(), meanR = reference.Take(n).Average();
        double covariance = 0, variance = 0;
        for (int i = 0; i < n; i++)
        {
            covariance += (asset[i] - meanA) * (reference[i] - meanR);
            variance += (reference[i] - meanR) * (reference[i] - meanR);
        }

        return variance > 1e-18 ? covariance / variance : null;
    }

    /// <summary>
    /// Each part's share of the whole's variance: weight × covariance(part, whole) ÷ variance(whole). The shares add up
    /// to 1 when the whole's return is the weighted sum of the parts' returns. A hedge shows as a negative share.
    /// </summary>
    public static double?[] VarianceShares(IReadOnlyList<double> weights, IReadOnlyList<IReadOnlyList<double>> partReturns, IReadOnlyList<double> wholeReturns)
    {
        double?[] shares = new double?[weights.Count];
        int n = wholeReturns.Count;
        if (n < 2)
        {
            return shares;
        }

        double meanW = wholeReturns.Average();
        double variance = wholeReturns.Sum(r => (r - meanW) * (r - meanW));
        if (variance <= 1e-18)
        {
            return shares;
        }

        for (int p = 0; p < weights.Count; p++)
        {
            IReadOnlyList<double> part = partReturns[p];
            int m = Math.Min(part.Count, n);
            // A part that started after the whole added nothing to the whole's return before it existed: those days
            // count as zeros. Measuring it over its own days only, against the whole's variance over all of them,
            // would shrink its share and the shares would no longer add up to 1.
            double meanP = part.Skip(part.Count - m).Sum() / n;
            double covariance = 0;
            for (int i = 0; i < n; i++)
            {
                // Aligned on the most recent days.
                double own = i >= n - m ? part[part.Count - n + i] : 0.0;
                covariance += (own - meanP) * (wholeReturns[i] - meanW);
            }

            shares[p] = weights[p] * covariance / variance;
        }

        return shares;
    }

    /// <summary>The fewest days of price history from which a share of risk is worked out.</summary>
    public const int MinDaysForShares = 20;

    /// <summary>
    /// Each asset's share of the risk of today's book: the covariance of what the asset's exposure would have made
    /// each past day with what the whole book would have made, over the variance of the whole. The shares of the
    /// assets that have price history add up to 1; a hedge shows as a negative share. It needs no history of the book
    /// itself, only of prices, so it works on the day a book is first read.
    /// </summary>
    /// <param name="exposures">Signed exposure per asset.</param>
    /// <param name="returnsByAsset">Daily returns per asset, covering the same days.</param>
    /// <returns>A share for each asset with history; nothing for one without, or when the book does not move.</returns>
    public static Dictionary<string, double> ExposureVarianceShares(IReadOnlyDictionary<string, decimal> exposures, IReadOnlyDictionary<string, double[]> returnsByAsset)
    {
        List<(string Asset, double Exposure, double[] Returns)> covered = exposures
            .Where(e => e.Value != 0m && returnsByAsset.ContainsKey(e.Key))
            .Select(e => (e.Key, (double)e.Value, returnsByAsset[e.Key]))
            .ToList();
        Dictionary<string, double> shares = [];
        int days = covered.Count == 0 ? 0 : covered.Min(c => c.Returns.Length);
        if (days < MinDaysForShares)
        {
            return shares;
        }

        double[] whole = new double[days];
        foreach ((_, double exposure, double[] returns) in covered)
        {
            for (int t = 0; t < days; t++)
            {
                // Aligned on the most recent days.
                whole[t] += exposure * returns[returns.Length - days + t];
            }
        }

        double meanW = whole.Average();
        double variance = whole.Sum(w => (w - meanW) * (w - meanW));
        // Against the size of the book, so that a book of a few dollars is not taken for one that does not move.
        double scale = covered.Sum(c => Math.Abs(c.Exposure));
        if (variance <= 1e-18 * scale * scale)
        {
            return shares;
        }

        foreach ((string asset, double exposure, double[] returns) in covered)
        {
            double meanP = 0;
            for (int t = 0; t < days; t++)
            {
                meanP += exposure * returns[returns.Length - days + t];
            }

            meanP /= days;
            double covariance = 0;
            for (int t = 0; t < days; t++)
            {
                covariance += (exposure * returns[returns.Length - days + t] - meanP) * (whole[t] - meanW);
            }

            shares[asset] = covariance / variance;
        }

        return shares;
    }
}

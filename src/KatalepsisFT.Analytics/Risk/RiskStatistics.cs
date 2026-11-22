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
            double meanP = part.Take(m).Average();
            double covariance = 0;
            for (int i = 0; i < m; i++)
            {
                // Align on the most recent days.
                covariance += (part[part.Count - m + i] - meanP) * (wholeReturns[n - m + i] - meanW);
            }

            shares[p] = weights[p] * covariance / variance;
        }

        return shares;
    }
}

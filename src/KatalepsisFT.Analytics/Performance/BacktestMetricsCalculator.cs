// Copied from Katalepsis (Horizen.Ria/Backtests/BacktestMetricsCalculator.cs, commit 9847e4f). Only the namespace was changed.
namespace KatalepsisFT.Analytics.Performance;

/// <summary>
/// Metric definitions mirror Tools/StrategyResearch/research/metrics.py (<c>summarize</c>):
/// daily returns r = E[t] / E[t−1] − 1; n returns; years = n / 365;
/// CAGR = (E[n] / E[0])^(1/years) − 1; volatility = stdev(r, ddof 1) × √365; Sharpe = mean(r) × 365 / volatility (rf 0);
/// Sortino = mean(r) × 365 / (√(Σ r² over r &lt; 0 / n) × √365); drawdown = E / running peak − 1 with the starting NAV as the
/// first peak; Calmar = CAGR / |max drawdown|; turnover and cost drag = their daily sums / years; total return = E[n] / E[0] − 1.
/// </summary>
public static class BacktestMetricsCalculator
{
    private const int MetricDecimals = 6;

    /// <param name="equity">NAV at each close, first element = starting NAV.</param>
    /// <param name="turnover">One-way turnover per close (same length as <paramref name="equity"/>), initial allocation excluded.</param>
    /// <param name="costs">Costs per close as a fraction of NAV (same length as <paramref name="equity"/>).</param>
    public static BacktestMetrics Compute(IReadOnlyList<decimal> equity, IReadOnlyList<decimal>? turnover = null, IReadOnlyList<decimal>? costs = null)
    {
        int n = Math.Max(0, equity.Count - 1);
        if (n == 0 || equity[0] <= 0m)
        {
            return new BacktestMetrics { TotalReturn = 0m, Turnover = 0m, CostDrag = 0m };
        }

        double[] r = new double[n];
        for (int t = 1; t <= n; t++)
        {
            r[t - 1] = equity[t - 1] > 0m ? (double)(equity[t] / equity[t - 1]) - 1.0 : 0.0;
        }

        double years = n / (double)BacktestConstants.DaysPerYear;
        double final = (double)(equity[n] / equity[0]);
        double? cagr = final > 0 ? Math.Pow(final, 1.0 / years) - 1.0 : null;
        double mean = r.Average();
        double? vol = null;
        double? downside = null;
        if (n > 1)
        {
            double variance = r.Sum(x => (x - mean) * (x - mean)) / (n - 1);
            vol = Math.Sqrt(variance) * Math.Sqrt(BacktestConstants.DaysPerYear);
            downside = Math.Sqrt(r.Where(x => x < 0).Sum(x => x * x) / n) * Math.Sqrt(BacktestConstants.DaysPerYear);
        }

        double meanAnnual = mean * BacktestConstants.DaysPerYear;
        double maxDrawdown = (double)DrawdownSeries(equity).Min();

        return new BacktestMetrics
        {
            Cagr = ToDecimal(cagr),
            Volatility = ToDecimal(vol),
            Sharpe = vol > 0 ? ToDecimal(meanAnnual / vol) : null,
            Sortino = downside > 0 ? ToDecimal(meanAnnual / downside) : null,
            MaxDrawdown = ToDecimal(maxDrawdown),
            Calmar = maxDrawdown < 0 && cagr is not null ? ToDecimal(cagr / Math.Abs(maxDrawdown)) : null,
            Turnover = ToDecimal(turnover is null ? 0.0 : (double)turnover.Sum() / years),
            CostDrag = ToDecimal(costs is null ? 0.0 : (double)costs.Sum() / years),
            TotalReturn = ToDecimal(final - 1.0),
        };
    }

    /// <summary>E / running peak − 1, where the starting NAV counts as the first peak (0 at the start, ≤ 0 afterwards).</summary>
    public static IReadOnlyList<decimal> DrawdownSeries(IReadOnlyList<decimal> equity)
    {
        var result = new decimal[equity.Count];
        decimal peak = equity.Count > 0 ? equity[0] : 0m;
        for (int i = 0; i < equity.Count; i++)
        {
            peak = Math.Max(peak, equity[i]);
            result[i] = peak > 0m ? equity[i] / peak - 1m : 0m;
        }

        return result;
    }

    private static decimal? ToDecimal(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        // Guard against decimal overflow on absurd values (e.g. CAGR of a few days); keep the sign.
        double clamped = Math.Clamp(value.Value, -1e15, 1e15);
        return Math.Round((decimal)clamped, MetricDecimals);
    }
}

/// <summary>Picks at most <c>maxPoints</c> evenly spaced indices, always keeping the first, the last and an optional key point (the trough).</summary>
public static class SeriesDownsampler
{
    public static IReadOnlyList<int> Indices(int count, int maxPoints = BacktestConstants.MaxChartPoints, int? mustInclude = null)
    {
        if (count <= 0)
        {
            return Array.Empty<int>();
        }

        maxPoints = Math.Max(2, maxPoints);
        if (count <= maxPoints)
        {
            return Enumerable.Range(0, count).ToArray();
        }

        bool extra = mustInclude is int m && m > 0 && m < count - 1;
        int gridPoints = extra ? maxPoints - 1 : maxPoints;
        var set = new SortedSet<int>();
        for (int k = 0; k < gridPoints; k++)
        {
            set.Add((int)Math.Round(k * (count - 1) / (double)(gridPoints - 1), MidpointRounding.AwayFromZero));
        }

        if (extra)
        {
            set.Add(mustInclude!.Value);
        }

        return set.ToArray();
    }
}

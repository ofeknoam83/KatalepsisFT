using KatalepsisFT.Analytics.Statistics;

namespace KatalepsisFT.Analytics.Performance;

/// <summary>How a return series relates to its benchmark. Null where a number is undefined, for example beta against a flat benchmark.</summary>
public sealed record BenchmarkComparison(double ExcessReturn, double? Beta, double? Correlation, double? TrackingError, double? InformationRatio)
{
    /// <summary>
    /// The fewest daily returns from which beta and correlation are worked out. With fewer the answer is noise: two days
    /// always give a correlation of exactly 1 or -1, and a beta of any size. The risk page uses the same number.
    /// </summary>
    public const int MinDaysForBeta = 20;

    /// <summary>
    /// Excess return is the difference of the two compounded returns. Beta is covariance of the daily returns over the benchmark's variance. Tracking
    /// error is the annualised standard deviation of the daily return differences (365-day year, as everywhere else).
    /// The information ratio is the annualised mean daily difference over the tracking error.
    /// </summary>
    public static BenchmarkComparison Compute(IReadOnlyList<double> portfolio, IReadOnlyList<double> benchmark)
    {
        if (portfolio.Count != benchmark.Count)
        {
            throw new ArgumentException("The two return series must cover the same days.", nameof(benchmark));
        }

        double excess = ReturnCalculator.Compound(portfolio) - ReturnCalculator.Compound(benchmark);
        int n = portfolio.Count;
        if (n < 2)
        {
            return new BenchmarkComparison(excess, null, null, null, null);
        }

        double meanP = portfolio.Average(), meanB = benchmark.Average();
        double covariance = 0, varianceP = 0, varianceB = 0;
        for (int i = 0; i < n; i++)
        {
            double dp = portfolio[i] - meanP, db = benchmark[i] - meanB;
            covariance += dp * db;
            varianceP += dp * dp;
            varianceB += db * db;
        }

        covariance /= n - 1;
        varianceP /= n - 1;
        varianceB /= n - 1;

        // A series that does not move has no beta against it and no correlation with anything, and neither does a
        // history too short to tell.
        const double Flat = 1e-18;
        bool enough = n >= MinDaysForBeta;
        double? beta = enough && varianceB > Flat ? covariance / varianceB : null;
        double? correlation = enough && varianceB > Flat && varianceP > Flat ? covariance / Math.Sqrt(varianceP * varianceB) : null;

        double[] differences = portfolio.Zip(benchmark, (p, b) => p - b).ToArray();
        double trackingError = Indicators.StdDev(differences) * Math.Sqrt(Indicators.DaysPerYear);
        double? informationRatio = trackingError > 1e-9 ? differences.Average() * Indicators.DaysPerYear / trackingError : null;
        return new BenchmarkComparison(excess, beta, correlation, trackingError, informationRatio);
    }
}

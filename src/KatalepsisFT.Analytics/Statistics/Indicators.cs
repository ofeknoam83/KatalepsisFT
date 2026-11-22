// Copied from Katalepsis (Horizen.Ria/Engine/Common/Indicators.cs, commit 9847e4f). Only the namespace was changed.
namespace KatalepsisFT.Analytics.Statistics;

/// <summary>
/// Indicators on daily closes (oldest first). Mirrors Tools/StrategyResearch/research/strategies.py:
/// NaN unless the full window exists, so a short history is never silently treated as a full one.
/// Doubles are used here only; callers convert results back to decimal.
/// </summary>
public static class Indicators
{
    public const double DaysPerYear = 365d;

    /// <summary>Last <paramref name="window"/> simple daily returns, or null when fewer than window + 1 closes exist.</summary>
    public static double[]? Returns(IReadOnlyList<decimal> closes, int window)
    {
        if (window < 1 || closes.Count < window + 1)
        {
            return null;
        }

        var result = new double[window];
        int offset = closes.Count - window - 1;
        for (int i = 0; i < window; i++)
        {
            double previous = (double)closes[offset + i];
            double current = (double)closes[offset + i + 1];
            if (previous <= 0d)
            {
                return null;
            }

            result[i] = current / previous - 1d;
        }

        return result;
    }

    /// <summary>Annualised standard deviation (ddof 1) of the last <paramref name="window"/> daily returns.</summary>
    public static double RealizedVol(IReadOnlyList<decimal> closes, int window)
    {
        double[]? returns = Returns(closes, window);
        if (returns is null || returns.Length < 2)
        {
            return double.NaN;
        }

        return StdDev(returns) * Math.Sqrt(DaysPerYear);
    }

    public static double Sma(IReadOnlyList<decimal> closes, int window)
    {
        if (window < 1 || closes.Count < window)
        {
            return double.NaN;
        }

        double sum = 0d;
        for (int i = closes.Count - window; i < closes.Count; i++)
        {
            sum += (double)closes[i];
        }

        return sum / window;
    }

    /// <summary><paramref name="days"/>-day return; NaN when the history is shorter.</summary>
    public static double Return(IReadOnlyList<decimal> closes, int days)
    {
        if (days < 1 || closes.Count <= days || closes[^(days + 1)] <= 0m)
        {
            return double.NaN;
        }

        return (double)closes[^1] / (double)closes[^(days + 1)] - 1d;
    }

    /// <summary>Last close relative to the highest close of the last <paramref name="lookback"/> days (0 = at the high, −0.2 = 20% below).</summary>
    public static double DrawdownFromHigh(IReadOnlyList<decimal> closes, int lookback)
    {
        if (lookback < 1 || closes.Count < lookback)
        {
            return double.NaN;
        }

        decimal high = 0m;
        for (int i = closes.Count - lookback; i < closes.Count; i++)
        {
            high = Math.Max(high, closes[i]);
        }

        return high <= 0m ? double.NaN : (double)closes[^1] / (double)high - 1d;
    }

    /// <summary>
    /// Matrix of the last <paramref name="window"/> daily returns (rows = days, columns = assets), aligned on the most
    /// recent close of each series; null when any series is too short.
    /// </summary>
    public static double[,]? ReturnMatrix(IReadOnlyList<IReadOnlyList<decimal>> series, int window)
    {
        var matrix = new double[window, series.Count];
        for (int j = 0; j < series.Count; j++)
        {
            double[]? returns = Returns(series[j], window);
            if (returns is null)
            {
                return null;
            }

            for (int i = 0; i < window; i++)
            {
                matrix[i, j] = returns[i];
            }
        }

        return matrix;
    }

    /// <summary>Sample covariance with ddof 1 (pandas DataFrame.cov()).</summary>
    public static double[,] Covariance(double[,] returns)
    {
        int n = returns.GetLength(0);
        int p = returns.GetLength(1);
        double[] means = ColumnMeans(returns);
        var cov = new double[p, p];
        for (int a = 0; a < p; a++)
        {
            for (int b = a; b < p; b++)
            {
                double sum = 0d;
                for (int i = 0; i < n; i++)
                {
                    sum += (returns[i, a] - means[a]) * (returns[i, b] - means[b]);
                }

                cov[a, b] = cov[b, a] = n > 1 ? sum / (n - 1) : 0d;
            }
        }

        return cov;
    }

    /// <summary>Ledoit-Wolf (2004) shrinkage of the sample covariance towards a scaled identity (same formula as research/strategies.py).</summary>
    public static double[,] LedoitWolf(double[,] returns)
    {
        int n = returns.GetLength(0);
        int p = returns.GetLength(1);
        double[] means = ColumnMeans(returns);
        var x = new double[n, p];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < p; j++)
            {
                x[i, j] = returns[i, j] - means[j];
            }
        }

        var sample = new double[p, p];
        for (int a = 0; a < p; a++)
        {
            for (int b = 0; b < p; b++)
            {
                double sum = 0d;
                for (int i = 0; i < n; i++)
                {
                    sum += x[i, a] * x[i, b];
                }

                sample[a, b] = sum / n;
            }
        }

        double mu = 0d;
        for (int a = 0; a < p; a++)
        {
            mu += sample[a, a];
        }

        mu /= p;
        double delta = 0d;
        for (int a = 0; a < p; a++)
        {
            for (int b = 0; b < p; b++)
            {
                double diff = sample[a, b] - (a == b ? mu : 0d);
                delta += diff * diff;
            }
        }

        delta /= p;
        double beta = 0d;
        for (int i = 0; i < n; i++)
        {
            for (int a = 0; a < p; a++)
            {
                for (int b = 0; b < p; b++)
                {
                    double diff = x[i, a] * x[i, b] - sample[a, b];
                    beta += diff * diff;
                }
            }
        }

        beta /= (double)n * n * p;
        double shrink = delta > 0d ? Math.Clamp(beta / delta, 0d, 1d) : 1d;
        var result = new double[p, p];
        for (int a = 0; a < p; a++)
        {
            for (int b = 0; b < p; b++)
            {
                result[a, b] = shrink * (a == b ? mu : 0d) + (1d - shrink) * sample[a, b];
            }
        }

        return result;
    }

    /// <summary>
    /// Long-only minimum-variance weights with a per-asset cap (sum = 1), by projected gradient descent onto the capped
    /// simplex. Deterministic; converges for any positive semi-definite covariance.
    /// </summary>
    public static double[] MinVarianceWeights(double[,] cov, double cap, int maxIterations = 20_000)
    {
        int p = cov.GetLength(0);
        if (p == 0)
        {
            return [];
        }

        cap = Math.Max(cap, 1d / p);
        double lipschitz = 0d;
        for (int a = 0; a < p; a++)
        {
            double row = 0d;
            for (int b = 0; b < p; b++)
            {
                row += Math.Abs(cov[a, b]);
            }

            lipschitz = Math.Max(lipschitz, 2d * row);
        }

        var w = new double[p];
        Array.Fill(w, 1d / p);
        if (lipschitz <= 0d)
        {
            return w;
        }

        double step = 1d / lipschitz;
        var next = new double[p];
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            for (int a = 0; a < p; a++)
            {
                double gradient = 0d;
                for (int b = 0; b < p; b++)
                {
                    gradient += 2d * cov[a, b] * w[b];
                }

                next[a] = w[a] - step * gradient;
            }

            double[] projected = ProjectOntoCappedSimplex(next, cap);
            double change = 0d;
            for (int a = 0; a < p; a++)
            {
                change = Math.Max(change, Math.Abs(projected[a] - w[a]));
            }

            w = projected;
            if (change < 1e-13)
            {
                break;
            }
        }

        return w;
    }

    /// <summary>Euclidean projection onto { 0 ≤ w ≤ cap, Σw = 1 } by bisection on the shift τ.</summary>
    public static double[] ProjectOntoCappedSimplex(double[] v, double cap)
    {
        double low = v.Min() - cap;
        double high = v.Max();
        for (int i = 0; i < 200; i++)
        {
            double tau = (low + high) / 2d;
            double sum = v.Sum(x => Math.Clamp(x - tau, 0d, cap));
            if (sum > 1d)
            {
                low = tau;
            }
            else
            {
                high = tau;
            }
        }

        double shift = (low + high) / 2d;
        double[] result = v.Select(x => Math.Clamp(x - shift, 0d, cap)).ToArray();
        double total = result.Sum();
        return total > 0d ? result.Select(x => x / total).ToArray() : v.Select(_ => 1d / v.Length).ToArray();
    }

    public static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return double.NaN;
        }

        double mean = values.Average();
        double sum = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sum / (values.Count - 1));
    }

    private static double[] ColumnMeans(double[,] matrix)
    {
        int n = matrix.GetLength(0);
        int p = matrix.GetLength(1);
        var means = new double[p];
        for (int j = 0; j < p; j++)
        {
            double sum = 0d;
            for (int i = 0; i < n; i++)
            {
                sum += matrix[i, j];
            }

            means[j] = n > 0 ? sum / n : 0d;
        }

        return means;
    }
}

// Copied from Katalepsis (Horizen.Ria.Tests/Backtests/BacktestMetricsTests.cs, commit 9847e4f).
// The two tests that need the backtest simulator and the option pricer were left behind; the rest is unchanged.
using KatalepsisFT.Analytics.Performance;
using Shouldly;

namespace KatalepsisFT.Analytics.Tests.Performance;

public class BacktestMetricsTests
{
    [Fact]
    public void Metrics_match_hand_computed_values()
    {
        // Returns +10%, −10%, +10% over 3 days; values from the formulas of research/metrics.py.
        decimal[] equity = [100m, 110m, 99m, 108.9m];
        decimal[] turnover = [0m, 0m, 0.5m, 0m];
        decimal[] costs = [0m, 0.001m, 0m, 0m];

        BacktestMetrics m = BacktestMetricsCalculator.Compute(equity, turnover, costs);

        m.TotalReturn.ShouldBe(0.089m);
        m.Volatility!.Value.ShouldBe(2.206052m, 0.000001m);
        m.Sharpe!.Value.ShouldBe(5.515131m, 0.000001m);
        m.Sortino!.Value.ShouldBe(11.030261m, 0.000001m);
        m.MaxDrawdown.ShouldBe(-0.1m);
        m.Cagr!.Value.ShouldBe(31992.275338m, 0.0001m);
        m.Calmar!.Value.ShouldBe(319922.75338m, 0.001m);
        m.Turnover!.Value.ShouldBe(60.833333m, 0.000001m);
        m.CostDrag!.Value.ShouldBe(0.121667m, 0.000001m);
    }

    [Fact]
    public void Cagr_annualises_over_365_day_years()
    {
        // Doubling over 730 daily returns → (2)^(1/2) − 1.
        decimal[] equity = Enumerable.Range(0, 731).Select(k => 100m * (decimal)Math.Pow(2, k / 730.0)).ToArray();

        BacktestMetrics m = BacktestMetricsCalculator.Compute(equity);

        m.Cagr!.Value.ShouldBe(0.414214m, 0.000002m);
        m.MaxDrawdown.ShouldBe(0m);
        m.Calmar.ShouldBeNull("no drawdown");
        m.Sortino.ShouldBeNull("no down days");
        m.Volatility!.Value.ShouldBeLessThan(0.0001m);
    }

    [Fact]
    public void Flat_series_has_no_sharpe()
    {
        BacktestMetrics m = BacktestMetricsCalculator.Compute([100m, 100m, 100m]);

        m.Sharpe.ShouldBeNull();
        m.Volatility.ShouldBe(0m);
        m.TotalReturn.ShouldBe(0m);
        m.Cagr.ShouldBe(0m);
    }

    [Fact]
    public void Drawdown_counts_the_starting_nav_as_the_first_peak()
    {
        BacktestMetricsCalculator.DrawdownSeries([100m, 90m, 95m, 120m, 60m]).ShouldBe([0m, -0.1m, -0.05m, 0m, -0.5m]);
    }

    [Theory]
    [InlineData(1000, 400)]
    [InlineData(401, 400)]
    [InlineData(5000, 400)]
    [InlineData(1000, 50)]
    public void Downsampling_keeps_at_most_max_points_with_both_ends(int count, int max)
    {
        IReadOnlyList<int> indices = SeriesDownsampler.Indices(count, max, mustInclude: count / 3 + 1);

        indices.Count.ShouldBeLessThanOrEqualTo(max);
        indices.Count.ShouldBeGreaterThan(max - 3);
        indices[0].ShouldBe(0);
        indices[^1].ShouldBe(count - 1);
        indices.ShouldContain(count / 3 + 1);
        indices.ShouldBe(indices.OrderBy(i => i).Distinct().ToList());
    }

    [Fact]
    public void Short_series_are_not_downsampled()
    {
        SeriesDownsampler.Indices(400).ShouldBe(Enumerable.Range(0, 400).ToArray());
        SeriesDownsampler.Indices(3).ShouldBe([0, 1, 2]);
        SeriesDownsampler.Indices(0).ShouldBeEmpty();
    }
}

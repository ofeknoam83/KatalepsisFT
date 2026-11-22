// Copied from Katalepsis (Horizen.Ria.Tests/Engine/EngineMathTests.cs, class IndicatorTests, commit 9847e4f). Unchanged.
using KatalepsisFT.Analytics.Statistics;
using Shouldly;

namespace KatalepsisFT.Analytics.Tests.Statistics;

public class IndicatorTests
{
    [Fact]
    public void Sma_and_return_need_the_full_window()
    {
        List<decimal> closes = EngineTestData.Line(1m, 10m, 10);

        Indicators.Sma(closes, 4).ShouldBe(8.5d, 1e-9);
        double.IsNaN(Indicators.Sma(closes, 11)).ShouldBeTrue();
        Indicators.Return(closes, 9).ShouldBe(9d, 1e-9);
        double.IsNaN(Indicators.Return(closes, 10)).ShouldBeTrue();
    }

    [Fact]
    public void Realized_vol_is_annualised_sample_std_of_daily_returns()
    {
        List<decimal> closes = [100m, 110m, 99m, 108.9m];

        double vol = Indicators.RealizedVol(closes, 3);

        double expected = Indicators.StdDev([0.1, -0.1, 0.1]) * Math.Sqrt(365d);
        vol.ShouldBe(expected, 1e-9);
        double.IsNaN(Indicators.RealizedVol(closes, 4)).ShouldBeTrue();
    }

    [Fact]
    public void Drawdown_from_high_uses_the_lookback_window()
    {
        List<decimal> closes = [50m, 200m, 100m, 120m, 90m];

        Indicators.DrawdownFromHigh(closes, 4).ShouldBe(-0.55d, 1e-9);
        Indicators.DrawdownFromHigh(closes, 3).ShouldBe(-0.25d, 1e-9);
    }

    [Fact]
    public void Ledoit_wolf_is_symmetric_and_between_sample_and_target()
    {
        double[,] returns = Matrix(
            EngineTestData.Walk(100m, 61, 0, 0.03, 1),
            EngineTestData.Walk(100m, 61, 0, 0.05, 2),
            EngineTestData.Walk(100m, 61, 0, 0.02, 3)
        );

        double[,] shrunk = Indicators.LedoitWolf(returns);

        for (int a = 0; a < 3; a++)
        {
            shrunk[a, a].ShouldBeGreaterThan(0d);
            for (int b = 0; b < 3; b++)
            {
                shrunk[a, b].ShouldBe(shrunk[b, a], 1e-15);
            }
        }
    }

    [Fact]
    public void Min_variance_respects_cap_and_beats_equal_weights()
    {
        double[,] cov = { { 0.04, 0.01, 0.0 }, { 0.01, 0.09, 0.0 }, { 0.0, 0.0, 0.01 } };

        double[] w = Indicators.MinVarianceWeights(cov, 0.5);

        w.Sum().ShouldBe(1d, 1e-9);
        w.ShouldAllBe(x => x >= -1e-12 && x <= 0.5 + 1e-9);
        w[2].ShouldBe(0.5d, 1e-6);
        Variance(cov, w).ShouldBeLessThan(Variance(cov, [1d / 3, 1d / 3, 1d / 3]));
    }

    [Fact]
    public void Min_variance_matches_the_unconstrained_solution_when_the_cap_does_not_bind()
    {
        double[,] cov = { { 0.04, 0.0 }, { 0.0, 0.01 } };

        double[] w = Indicators.MinVarianceWeights(cov, 1.0);

        w[0].ShouldBe(0.2d, 1e-6);
        w[1].ShouldBe(0.8d, 1e-6);
    }

    [Fact]
    public void Projection_onto_the_capped_simplex_is_feasible()
    {
        double[] projected = Indicators.ProjectOntoCappedSimplex([0.9, 0.5, -0.4, 0.1], 0.4);

        projected.Sum().ShouldBe(1d, 1e-9);
        projected.ShouldAllBe(x => x >= 0d && x <= 0.4 + 1e-9);
    }

    private static double Variance(double[,] cov, double[] w)
    {
        double v = 0d;
        for (int a = 0; a < w.Length; a++)
        {
            for (int b = 0; b < w.Length; b++)
            {
                v += w[a] * cov[a, b] * w[b];
            }
        }

        return v;
    }

    private static double[,] Matrix(params List<decimal>[] series) => Indicators.ReturnMatrix(series, 60)!;
}

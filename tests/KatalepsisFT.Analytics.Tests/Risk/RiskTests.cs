using KatalepsisFT.Analytics.Risk;
using Shouldly;

namespace KatalepsisFT.Analytics.Tests.Risk;

public class ScenarioCalculatorTests
{
    private static readonly AssetExposureInput[] Book = [new("BTC", 100m, 1.0), new("ETH", 50m, 1.2), new("SOL", -40m, 1.5)];

    [Fact]
    public void ReferenceMovesByTheShock_OthersThroughTheirBeta_ShortsGainOnAFall()
    {
        // BTC −10: −10. ETH −12%: −6. SOL short, −15%: +6.
        ScenarioCalculator.Pnl(Book, "BTC", -0.10, 1.0).ShouldBe(-10m);
        ScenarioCalculator.Pnl(Book, "BTC", 0.10, 1.0).ShouldBe(10m);
    }

    [Fact]
    public void BetaMultiplier_ScalesOnlyTheOtherAssets()
    {
        // BTC −10. ETH −18%: −9. SOL short, −22.5%: +9.
        ScenarioCalculator.Pnl(Book, "btc", -0.10, 1.5).ShouldBe(-10m);
        ScenarioCalculator.Pnl([new("ETH", 100m, 1.2)], "BTC", -0.10, 1.5).ShouldBe(-18m);
    }

    [Fact]
    public void AnAssetCannotLoseMoreThanEverything()
    {
        ScenarioCalculator.Pnl([new("ALT", 100m, 3.0)], "BTC", -0.5, 1.0).ShouldBe(-100m);
        ScenarioCalculator.Pnl([new("ALT", -100m, 3.0)], "BTC", -0.5, 1.0).ShouldBe(100m, "a short gains at most the whole position");
    }
}

public class HistoricalVarTests
{
    [Fact]
    public void HypotheticalPnl_HoldsTodaysExposuresThroughEachPastDay()
    {
        decimal[] pnl = HistoricalVar.HypotheticalPnl(
            new Dictionary<string, decimal> { ["BTC"] = 100m, ["ETH"] = -50m, ["NEW"] = 10m },
            new Dictionary<string, double[]> { ["BTC"] = [0.10, -0.10], ["ETH"] = [0.20, -0.20] }
        );

        pnl.ShouldBe([0m, 0m], "the short offsets the long exactly; an asset without history adds nothing");
    }

    [Fact]
    public void Var_IsTheLossExceededOnFivePercentOfDays_AndShortfallTheAverageBeyondIt()
    {
        // 100 days: losses of 1…100.
        decimal[] pnl = Enumerable.Range(1, 100).Select(i => -(decimal)i).ToArray();

        VarResult result = HistoricalVar.Compute(pnl);

        result.Days.ShouldBe(100);
        result.ValueAtRiskUsd.ShouldBe(96m, "the five worst days lost 100, 99, 98, 97, 96");
        result.ExpectedShortfallUsd.ShouldBe(98m);
    }

    [Fact]
    public void ABookThatNeverLost_HasNoVar_AndAnEmptyHistoryGivesZero()
    {
        HistoricalVar.Compute([1m, 2m, 3m]).ShouldBe(new VarResult(0m, 0m, 3));
        HistoricalVar.Compute([]).ShouldBe(new VarResult(0m, 0m, 0));
    }

    [Fact]
    public void AShortHistory_UsesItsWorstDay()
    {
        HistoricalVar.Compute([-5m, 3m, -1m]).ShouldBe(new VarResult(5m, 5m, 3));
    }
}

public class RiskStatisticsTests
{
    private static readonly double[] Returns = [0.02, -0.01, 0.03, -0.02, 0.01, 0.00, -0.015, 0.025];

    [Fact]
    public void Volatility_UsesTheLastWindow_AndIsNullWhenHistoryIsShorter()
    {
        double expected = Statistics.StdDev(Returns[^4..]) * Math.Sqrt(365);

        RiskStatistics.Volatility(Returns, 4)!.Value.ShouldBe(expected, 1e-12);
        RiskStatistics.Volatility(Returns, 9).ShouldBeNull();
    }

    [Fact]
    public void RollingVolatility_HasOneValuePerFullWindow_EndingWithTheLatest()
    {
        double[] rolling = RiskStatistics.RollingVolatility(Returns, 4);

        rolling.Length.ShouldBe(5);
        rolling[^1].ShouldBe(RiskStatistics.Volatility(Returns, 4)!.Value, 1e-12);
        RiskStatistics.RollingVolatility(Returns, 20).ShouldBeEmpty();
    }

    [Fact]
    public void CorrelationAndBeta_OfScaledAndFlippedSeries()
    {
        double[] twice = Returns.Select(r => 2 * r).ToArray();
        double[] flipped = Returns.Select(r => -r).ToArray();

        RiskStatistics.Correlation(Returns, twice)!.Value.ShouldBe(1.0, 1e-12);
        RiskStatistics.Correlation(Returns, flipped)!.Value.ShouldBe(-1.0, 1e-12);
        RiskStatistics.Beta(twice, Returns)!.Value.ShouldBe(2.0, 1e-12);
        RiskStatistics.Correlation(Returns, new double[Returns.Length]).ShouldBeNull();
        RiskStatistics.Beta(Returns, new double[Returns.Length]).ShouldBeNull();
    }

    [Fact]
    public void VarianceShares_AddUpToOne_AndAHedgeIsNegative()
    {
        double[] hedge = Returns.Select(r => -0.5 * r).ToArray();
        double[] whole = Returns.Zip(hedge, (a, b) => 0.6 * a + 0.4 * b).ToArray();

        double?[] shares = RiskStatistics.VarianceShares([0.6, 0.4], [Returns, hedge], whole);

        (shares[0]!.Value + shares[1]!.Value).ShouldBe(1.0, 1e-9);
        shares[0]!.Value.ShouldBeGreaterThan(1.0);
        shares[1]!.Value.ShouldBeLessThan(0.0);
    }

    [Fact]
    public void VarianceShares_StillAddUpToOne_WhenAPartStartedLater()
    {
        // The second part joins half way: before that the whole is the first part alone.
        int half = Returns.Length / 2;
        double[] late = Returns.Skip(half).Select(r => 0.5 * r + 0.001).ToArray();
        double[] whole = Returns.Select((r, i) => i < half ? 0.6 * r : 0.6 * r + 0.4 * late[i - half]).ToArray();

        double?[] shares = RiskStatistics.VarianceShares([0.6, 0.4], [Returns, late], whole);

        (shares[0]!.Value + shares[1]!.Value).ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void VarianceShares_OfAFlatWhole_AreUndefined()
    {
        RiskStatistics.VarianceShares([1.0], [Returns], new double[Returns.Length]).ShouldBe([null]);
    }

    private static class Statistics
    {
        public static double StdDev(double[] values) => KatalepsisFT.Analytics.Statistics.Indicators.StdDev(values);
    }
}

public class LiquidityCalculatorTests
{
    [Fact]
    public void Lines_DaysAreGrossOverTheShareOfVolumeWeTrade_SlowestFirst()
    {
        IReadOnlyList<LiquidityLine> lines = LiquidityCalculator.Lines([new("BTC", 50m, 10_000m), new("ONDO", 16m, 10m), new("NEW", 3m, null), new("FLAT", 0m, 5m)], 0.10m);

        lines.Select(l => l.Asset).ShouldBe(["NEW", "ONDO", "BTC"], "unknown volume is the slowest; a zero position is left out");
        lines[1].DailyCapacityUsd.ShouldBe(1m);
        lines[1].Days!.Value.ShouldBe(16.0, 1e-9);
        lines[2].Days!.Value.ShouldBe(0.05, 1e-9);
        lines[0].Days.ShouldBeNull();
    }

    [Fact]
    public void Ladder_SplitsEachPositionAcrossTheBuckets_AndCashIsImmediate()
    {
        // ONDO: 30 gross at 1 a day → 1 within a day, 4 more within five, 15 more within twenty, 10 beyond.
        IReadOnlyList<LiquidityLine> lines = LiquidityCalculator.Lines([new("ONDO", 30m, 10m), new("BTC", 50m, 10_000m), new("NEW", 3m, null)], 0.10m);

        LiquidityLadder ladder = LiquidityCalculator.Ladder(lines, cashUsd: 20m);

        ladder.WithinOneDayUsd.ShouldBe(20m + 50m + 1m);
        ladder.OneToFiveDaysUsd.ShouldBe(4m);
        ladder.FiveToTwentyDaysUsd.ShouldBe(15m);
        ladder.BeyondTwentyDaysUsd.ShouldBe(10m);
        ladder.UnknownUsd.ShouldBe(3m);
        ladder.TotalUsd.ShouldBe(103m);
    }

    [Fact]
    public void HalvingParticipation_DoublesTheDays()
    {
        LiquidityCalculator.Lines([new("ONDO", 16m, 10m)], 0.05m)[0].Days!.Value.ShouldBe(32.0, 1e-9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void ParticipationOutsideZeroToOne_IsRefused(double participation)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => LiquidityCalculator.Lines([], (decimal)participation));
    }
}

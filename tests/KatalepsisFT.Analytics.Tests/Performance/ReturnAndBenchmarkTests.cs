using KatalepsisFT.Analytics.Performance;
using Shouldly;

namespace KatalepsisFT.Analytics.Tests.Performance;

public class ReturnCalculatorTests
{
    [Fact]
    public void DailyReturns_WithoutFlows_AreNavRatios()
    {
        double[] returns = ReturnCalculator.DailyReturns([100m, 110m, 99m]);

        returns[0].ShouldBe(0.10, 1e-12);
        returns[1].ShouldBe(-0.10, 1e-12);
    }

    [Fact]
    public void DailyReturns_ADepositIsNotPerformance()
    {
        // 100 → 150 on a day when 50 came in: nothing was earned.
        double[] returns = ReturnCalculator.DailyReturns([100m, 150m], [0m, 50m]);

        returns[0].ShouldBe(0.0, 1e-12);
    }

    [Fact]
    public void DailyReturns_MoneyInArrivesBeforeTheDaysMove_AsInKatalepsis()
    {
        // 100 + 100 deposited, then +10% on the 200: NAV 220.
        double[] withDeposit = ReturnCalculator.DailyReturns([100m, 220m], [0m, 100m]);

        withDeposit[0].ShouldBe(0.10, 1e-12);
    }

    [Fact]
    public void DailyReturns_MoneyOutLeavesAfterTheDaysMove()
    {
        // 100 loses 3% to 97, then 40 is withdrawn: NAV 57.
        double[] withWithdrawal = ReturnCalculator.DailyReturns([100m, 57m], [0m, -40m]);
        // A manager retired at 98 after starting the day at 100: the whole book leaves at what it was worth.
        double[] retiredDown = ReturnCalculator.DailyReturns([100m, 0m], [0m, -98m]);
        double[] retiredUp = ReturnCalculator.DailyReturns([100m, 0m], [0m, -102m]);
        // 100 gains 10% to 110, then 90 is withdrawn. Counted as leaving in the morning this would read +100%.
        double[] largeWithdrawal = ReturnCalculator.DailyReturns([100m, 20m], [0m, -90m]);

        withWithdrawal[0].ShouldBe(-0.03, 1e-12);
        retiredDown[0].ShouldBe(-0.02, 1e-12);
        retiredUp[0].ShouldBe(0.02, 1e-12);
        largeWithdrawal[0].ShouldBe(0.10, 1e-12);
    }

    [Fact]
    public void DailyReturns_StartingFromNothing_ReturnsZeroInsteadOfDividingByZero()
    {
        ReturnCalculator.DailyReturns([0m, 0m, 50m], [0m, 0m, 0m]).ShouldBe([0.0, 0.0]);
    }

    [Fact]
    public void DailyReturns_FlowsOfAnotherLength_AreRefused()
    {
        Should.Throw<ArgumentException>(() => ReturnCalculator.DailyReturns([1m, 2m], [0m]));
    }

    [Fact]
    public void GrowthIndex_CompoundsUnrounded_AndCompoundAgrees()
    {
        double[] returns = [0.10, -0.10, 0.10];

        decimal[] index = ReturnCalculator.GrowthIndex(returns);

        index.Length.ShouldBe(4);
        index[0].ShouldBe(1m);
        ((double)index[3]).ShouldBe(1.089, 1e-12);
        ReturnCalculator.Compound(returns).ShouldBe(0.089, 1e-12);
        ReturnCalculator.Compound([]).ShouldBe(0.0);
    }
}

public class BenchmarkComparisonTests
{
    private static readonly double[] Benchmark = [0.02, -0.01, 0.03, -0.02, 0.01, 0.00, -0.015, 0.025];

    [Fact]
    public void SameSeries_HasBetaOne_NoExcess_NoTrackingError()
    {
        BenchmarkComparison c = BenchmarkComparison.Compute(Benchmark, Benchmark);

        c.ExcessReturn.ShouldBe(0.0, 1e-12);
        c.Beta!.Value.ShouldBe(1.0, 1e-12);
        c.Correlation!.Value.ShouldBe(1.0, 1e-12);
        c.TrackingError!.Value.ShouldBe(0.0, 1e-12);
        c.InformationRatio.ShouldBeNull("there is no tracking error to divide by");
    }

    [Fact]
    public void TwiceTheBenchmark_HasBetaTwo_AndPerfectCorrelation()
    {
        BenchmarkComparison c = BenchmarkComparison.Compute(Benchmark.Select(r => 2 * r).ToArray(), Benchmark);

        c.Beta!.Value.ShouldBe(2.0, 1e-12);
        c.Correlation!.Value.ShouldBe(1.0, 1e-12);
        c.TrackingError!.Value.ShouldBeGreaterThan(0.0);
    }

    [Fact]
    public void ShortTheBenchmark_HasBetaMinusOne()
    {
        BenchmarkComparison c = BenchmarkComparison.Compute(Benchmark.Select(r => -r).ToArray(), Benchmark);

        c.Beta!.Value.ShouldBe(-1.0, 1e-12);
        c.Correlation!.Value.ShouldBe(-1.0, 1e-12);
    }

    [Fact]
    public void AConstantLead_GivesTheInformationRatioItsSign()
    {
        // 0.1% a day more than the benchmark on odd days, the same on even days.
        double[] portfolio = Benchmark.Select((r, i) => r + (i % 2 == 0 ? 0.001 : 0.0)).ToArray();

        BenchmarkComparison c = BenchmarkComparison.Compute(portfolio, Benchmark);

        c.ExcessReturn.ShouldBeGreaterThan(0.0);
        c.InformationRatio!.Value.ShouldBeGreaterThan(0.0);
        // Differences are 0.001, 0, 0.001, 0, …: standard deviation 0.000534522, times √365.
        c.TrackingError!.Value.ShouldBe(0.000534522 * Math.Sqrt(365), 1e-6);
    }

    [Fact]
    public void FlatBenchmark_HasNoBetaOrCorrelation_ButStillAnExcess()
    {
        BenchmarkComparison c = BenchmarkComparison.Compute(Benchmark, new double[Benchmark.Length]);

        c.Beta.ShouldBeNull();
        c.Correlation.ShouldBeNull();
        c.ExcessReturn.ShouldBe(ReturnCalculator.Compound(Benchmark), 1e-12);
    }

    [Fact]
    public void OneDay_GivesOnlyTheExcess_AndDifferentLengthsAreRefused()
    {
        BenchmarkComparison c = BenchmarkComparison.Compute([0.05], [0.02]);

        c.ExcessReturn.ShouldBe(0.03, 1e-12);
        (c.Beta, c.Correlation, c.TrackingError, c.InformationRatio).ShouldBe((null, null, null, null), "one day is not enough for any statistic");
        Should.Throw<ArgumentException>(() => BenchmarkComparison.Compute([0.1, 0.2], [0.1]));
    }
}

public class PnlAttributionTests
{
    private static AttributionRow Row(int key, int day, decimal quantity, decimal mark, decimal multiplier = 1m, bool cash = false) =>
        new(key, new DateOnly(2026, 1, 1).AddDays(day), quantity, multiplier, mark, cash);

    [Fact]
    public void LongEarnsOnARise_ShortEarnsOnAFall_CashEarnsNothing()
    {
        IReadOnlyDictionary<int, decimal> pnl = PnlAttribution.ByPosition(
            [Row(1, 0, 10m, 100m), Row(1, 1, 10m, 110m), Row(2, 0, -5m, 50m), Row(2, 1, -5m, 40m), Row(3, 0, 1000m, 1m, cash: true), Row(3, 1, 1000m, 1m, cash: true)]
        );

        pnl[1].ShouldBe(100m);
        pnl[2].ShouldBe(50m);
        pnl.ContainsKey(3).ShouldBeFalse();
    }

    [Fact]
    public void TheQuantityHeldAtThePreviousCloseEarnsTheMove()
    {
        // 10 held into day 1 (+5 each), then 20 held into day 2 (−2 each).
        IReadOnlyDictionary<int, decimal> pnl = PnlAttribution.ByPosition([Row(1, 0, 10m, 100m), Row(1, 1, 20m, 105m), Row(1, 2, 20m, 103m)]);

        pnl[1].ShouldBe(10m * 5m + 20m * -2m);
    }

    [Fact]
    public void AGapInTheDays_IsNotCountedAsAMove_AndTheMultiplierApplies()
    {
        IReadOnlyDictionary<int, decimal> pnl = PnlAttribution.ByPosition(
            [Row(1, 0, 10m, 100m), Row(1, 5, 10m, 200m), Row(2, 0, 2m, 1000m, multiplier: 5m), Row(2, 1, 2m, 1010m, multiplier: 5m)]
        );

        pnl[1].ShouldBe(0m, "the position was not held on the days in between");
        pnl[2].ShouldBe(100m);
    }
}

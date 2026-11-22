using KatalepsisFT.Analytics.Risk;
using KatalepsisFT.Domain.Funds;
using Shouldly;

namespace KatalepsisFT.Analytics.Tests.Risk;

public class MandateCheckerTests
{
    private static readonly MandateState State = new(0.65m, 0.42m, "ONDO", 0.175m, "Kestrel Momentum", -0.48, "ONDO", 10.9);

    [Fact]
    public void EachLimitIsComparedWithItsOwnFigure()
    {
        IReadOnlyList<MandateCheck> checks = MandateChecker.Check(
            [
                (MandateLimitKind.DaysToLiquidateMax, 5m),
                (MandateLimitKind.GrossExposureMax, 2.0m),
                (MandateLimitKind.SingleAssetMax, 0.15m),
                (MandateLimitKind.ManagerDrawdownMin, -0.25m),
                (MandateLimitKind.NetExposureMin, -0.05m),
                (MandateLimitKind.NetExposureMax, 0.05m),
            ],
            State
        );

        checks.Select(c => c.Kind).ShouldBe(checks.Select(c => c.Kind).Order(), "checks come in a fixed order");
        checks.Single(c => c.Kind == MandateLimitKind.GrossExposureMax).WithinLimit.ShouldBe(true);
        checks.Single(c => c.Kind == MandateLimitKind.NetExposureMin).WithinLimit.ShouldBe(true);
        checks.Single(c => c.Kind == MandateLimitKind.NetExposureMax).WithinLimit.ShouldBe(false, "42% net is above a 5% ceiling");
        MandateCheck single = checks.Single(c => c.Kind == MandateLimitKind.SingleAssetMax);
        single.WithinLimit.ShouldBe(false);
        single.Rule.ShouldBe("Largest single asset (ONDO)");
        MandateCheck drawdown = checks.Single(c => c.Kind == MandateLimitKind.ManagerDrawdownMin);
        drawdown.WithinLimit.ShouldBe(false, "−48% is deeper than −25%");
        drawdown.Current.ShouldBe(-0.48m);
        checks.Single(c => c.Kind == MandateLimitKind.DaysToLiquidateMax).WithinLimit.ShouldBe(false);
    }

    [Fact]
    public void ExactlyAtTheLimit_IsWithinIt()
    {
        MandateState atLimit = State with { GrossOverNav = 2.0m, LargestAssetOverNav = 0.15m, WorstManagerDrawdown = -0.25, SlowestDaysToLiquidate = 5.0 };

        MandateChecker
            .Check([(MandateLimitKind.GrossExposureMax, 2.0m), (MandateLimitKind.SingleAssetMax, 0.15m), (MandateLimitKind.ManagerDrawdownMin, -0.25m), (MandateLimitKind.DaysToLiquidateMax, 5m)], atLimit)
            .ShouldAllBe(c => c.WithinLimit == true);
    }

    [Fact]
    public void AFigureThatIsNotAvailable_IsNeitherPassedNorFailed()
    {
        MandateState unknown = State with { WorstManager = null, WorstManagerDrawdown = null, SlowestAsset = null, SlowestDaysToLiquidate = null };

        IReadOnlyList<MandateCheck> checks = MandateChecker.Check([(MandateLimitKind.ManagerDrawdownMin, -0.25m), (MandateLimitKind.DaysToLiquidateMax, 5m)], unknown);

        checks.ShouldAllBe(c => c.WithinLimit == null && c.Current == null);
        checks[0].Rule.ShouldBe("Deepest manager drawdown");
    }
}

using KatalepsisFT.Analytics.Valuation;
using KatalepsisFT.Domain.Instruments;
using Shouldly;

namespace KatalepsisFT.Analytics.Tests.Valuation;

public class PositionValuerTests
{
    [Fact]
    public void Spot_ValueAndExposureAreQuantityTimesPrice()
    {
        PositionValue value = PositionValuer.Value(new PositionInput(InstrumentType.Spot, 2m, 98_400m, EntryPrice: 90_000m));

        value.ShouldBe(new PositionValue(196_800m, 196_800m, 16_800m));
    }

    [Fact]
    public void ShortPerp_AddsOnlyItsPnlToNav_ButItsFullNotionalToExposure()
    {
        // Short 100 at 3,700, marked 3,650: gained 50 per unit.
        PositionValue value = PositionValuer.Value(new PositionInput(InstrumentType.Perp, -100m, 3_650m, EntryPrice: 3_700m));

        value.MarketValueUsd.ShouldBe(5_000m);
        value.ExposureUsd.ShouldBe(-365_000m);
        value.UnrealizedPnlUsd.ShouldBe(5_000m);
    }

    [Fact]
    public void Perp_WithoutEntryPrice_HasZeroPnlAndStillItsExposure()
    {
        PositionValue value = PositionValuer.Value(new PositionInput(InstrumentType.Perp, 10m, 200m));

        value.ShouldBe(new PositionValue(0m, 2_000m, 0m));
    }

    [Fact]
    public void Future_UsesTheContractMultiplier()
    {
        // 24 CME contracts of 5 BTC, entered 95,120, marked 98,655.
        PositionValue value = PositionValuer.Value(new PositionInput(InstrumentType.Future, 24m, 98_655m, Multiplier: 5m, EntryPrice: 95_120m));

        value.ExposureUsd.ShouldBe(11_838_600m);
        value.MarketValueUsd.ShouldBe(424_200m);
    }

    [Fact]
    public void ShortCall_HasNegativeValueAndNegativeDeltaExposure()
    {
        // Short 10 calls worth 1,800 each, delta 0.45, underlying at 98,400.
        PositionValue value = PositionValuer.Value(
            new PositionInput(InstrumentType.Option, -10m, 1_800m, EntryPrice: 2_500m, UnderlyingPrice: 98_400m, Delta: 0.45m)
        );

        value.MarketValueUsd.ShouldBe(-18_000m);
        value.ExposureUsd.ShouldBe(-442_800m);
        value.UnrealizedPnlUsd.ShouldBe(7_000m, "sold at 2,500, now 1,800");
    }

    [Fact]
    public void LongPut_HasPositiveValueAndNegativeExposure()
    {
        PositionValue value = PositionValuer.Value(new PositionInput(InstrumentType.Option, 5m, 700m, UnderlyingPrice: 98_400m, Delta: -0.3m));

        value.MarketValueUsd.ShouldBe(3_500m);
        value.ExposureUsd.ShouldBe(-147_600m);
    }

    [Fact]
    public void Option_WithoutDelta_IsRefusedRatherThanValuedAtZeroExposure()
    {
        Action act = () => PositionValuer.Value(new PositionInput(InstrumentType.Option, 1m, 100m, UnderlyingPrice: 1_000m));

        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Cash_CountsInNavAndHasNoExposure()
    {
        PositionValue value = PositionValuer.Value(new PositionInput(InstrumentType.Cash, 7_250_000m, 1m));

        value.ShouldBe(new PositionValue(7_250_000m, 0m, 0m));
    }

    [Fact]
    public void Equity_IsValuedLikeSpot()
    {
        PositionValue value = PositionValuer.Value(new PositionInput(InstrumentType.Equity, 16_005m, 312.40m, EntryPrice: 287.10m));

        value.MarketValueUsd.ShouldBe(4_999_962m);
        value.ExposureUsd.ShouldBe(4_999_962m);
        value.UnrealizedPnlUsd.ShouldBe(404_926.50m);
    }

    [Fact]
    public void ExposureSummary_OppositeSidesAddToGrossAndCancelInNet()
    {
        ExposureSummary summary = ExposureSummary.Of([4.2m, 3.5m, -2.4m, 2.6m, 0m]);

        summary.LongUsd.ShouldBe(10.3m);
        summary.ShortUsd.ShouldBe(-2.4m);
        summary.GrossUsd.ShouldBe(12.7m);
        summary.NetUsd.ShouldBe(7.9m);
    }
}

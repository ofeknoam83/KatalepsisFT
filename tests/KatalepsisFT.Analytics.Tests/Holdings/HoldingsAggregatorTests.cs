using KatalepsisFT.Analytics.Holdings;
using KatalepsisFT.Domain.Instruments;
using Shouldly;

namespace KatalepsisFT.Analytics.Tests.Holdings;

public class HoldingsAggregatorTests
{
    private static HoldingRow Row(int sleeve, int manager, string asset, InstrumentType type, decimal exposure, decimal? value = null) =>
        new(sleeve, $"S{sleeve}", manager, $"M{manager}", asset, type, asset, 1m, null, 1m, value ?? (type == InstrumentType.Perp ? 0m : exposure), exposure, 0m);

    private static HoldingRow Cash(int sleeve, int manager, decimal amount) =>
        new(sleeve, $"S{sleeve}", manager, $"M{manager}", "USDC", InstrumentType.Cash, "USDC", amount, 1m, 1m, amount, 0m, 0m);

    // Manager 1 is hedged on BTC; managers 2 and 3 are long ONDO and manager 4 is short it.
    private static readonly HoldingRow[] Book =
    [
        Row(1, 1, "BTC", InstrumentType.Spot, 50m),
        Row(1, 1, "BTC", InstrumentType.Perp, -50m, value: 2m),
        Cash(1, 1, 48m),
        Row(2, 2, "ONDO", InstrumentType.Spot, 4m),
        Row(2, 2, "SOL", InstrumentType.Spot, 6m),
        Cash(2, 2, 10m),
        Row(2, 3, "ONDO", InstrumentType.Perp, 3m),
        Cash(2, 3, 20m),
        Row(2, 4, "ONDO", InstrumentType.Perp, -2m),
        Row(2, 4, "LINK", InstrumentType.Spot, 5m),
        Cash(2, 4, 11m),
    ];

    [Fact]
    public void Totals_NavIncludesCashAndPerpPnl_ExposureIncludesNeither()
    {
        BookTotals totals = HoldingsAggregator.Totals(Book.Where(r => r.ManagerId == 1));

        totals.NavUsd.ShouldBe(100m, "50 spot + 2 perp PnL + 48 cash");
        totals.CashUsd.ShouldBe(48m);
        totals.LongUsd.ShouldBe(50m);
        totals.ShortUsd.ShouldBe(-50m);
        totals.GrossUsd.ShouldBe(100m);
        totals.NetUsd.ShouldBe(0m);
    }

    [Fact]
    public void ByAsset_OppositeManagers_AddToGrossCancelInNetAndAreFlagged()
    {
        AssetExposure ondo = HoldingsAggregator.ByAsset(Book).Single(a => a.Asset == "ONDO");

        ondo.LongUsd.ShouldBe(7m);
        ondo.ShortUsd.ShouldBe(-2m);
        ondo.GrossUsd.ShouldBe(9m);
        ondo.NetUsd.ShouldBe(5m);
        ondo.NetByManager.Keys.Order().ShouldBe([2, 3, 4]);
        ondo.NetByType[InstrumentType.Spot].ShouldBe(4m);
        ondo.NetByType[InstrumentType.Perp].ShouldBe(1m);
        ondo.SleeveIds.ShouldBe([2]);
        ondo.HasOffsettingManagers.ShouldBeTrue();
    }

    [Fact]
    public void ByAsset_IsOrderedByGross_LeavesCashOut_AndAHedgedManagerIsNotOffsetting()
    {
        IReadOnlyList<AssetExposure> assets = HoldingsAggregator.ByAsset(Book);

        assets.Select(a => a.Asset).ShouldBe(["BTC", "ONDO", "SOL", "LINK"]);
        assets[0].GrossUsd.ShouldBe(100m);
        assets[0].NetUsd.ShouldBe(0m);
        assets[0].HasOffsettingManagers.ShouldBeFalse("one manager on both sides is a hedge, not two managers disagreeing");
    }

    [Fact]
    public void ByType_SplitsLongAndShortPerInstrumentType()
    {
        IReadOnlyList<TypeExposure> types = HoldingsAggregator.ByType(Book);

        types.Select(t => t.Type).ShouldBe([InstrumentType.Spot, InstrumentType.Perp]);
        types[0].LongUsd.ShouldBe(65m);
        types[0].ShortUsd.ShouldBe(0m);
        types[1].LongUsd.ShouldBe(3m);
        types[1].ShortUsd.ShouldBe(-52m);
    }

    [Fact]
    public void Overlaps_ShareIsCommonExposureOverTheSmallerBook_AndCountsOppositeSides()
    {
        IReadOnlyList<ManagerOverlap> overlaps = HoldingsAggregator.Overlaps(Book.Where(r => r.SleeveId == 2));

        // 2 holds ONDO 4 + SOL 6 (gross 10); 3 holds ONDO 3 (gross 3): they share min(4,3) = 3 of the smaller book's 3.
        overlaps.Single(o => o is { ManagerA: 2, ManagerB: 3 }).ShouldBe(new ManagerOverlap(2, 3, 1m, 0));
        // 2 and 4 share ONDO min(4,2) = 2 of the smaller book's 7, on opposite sides.
        overlaps.Single(o => o is { ManagerA: 2, ManagerB: 4 }).ShouldBe(new ManagerOverlap(2, 4, 0.2857m, 1));
        overlaps.Single(o => o is { ManagerA: 3, ManagerB: 4 }).ShouldBe(new ManagerOverlap(3, 4, 0.6667m, 1));
    }

    [Fact]
    public void Overlaps_AHedgedManagerSharesNothing()
    {
        HoldingRow[] rows = [.. Book.Where(r => r.ManagerId == 1), Row(1, 9, "BTC", InstrumentType.Spot, 10m)];

        HoldingsAggregator.Overlaps(rows).Single().Share.ShouldBe(0m);
    }

    [Fact]
    public void EmptyBook_GivesZeroesAndEmptyLists()
    {
        HoldingsAggregator.Totals([]).ShouldBe(new BookTotals(0m, 0m, 0m, 0m));
        HoldingsAggregator.ByAsset([]).ShouldBeEmpty();
        HoldingsAggregator.Overlaps([]).ShouldBeEmpty();
    }
}

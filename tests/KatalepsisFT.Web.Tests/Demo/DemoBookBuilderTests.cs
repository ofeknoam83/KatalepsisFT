using FluentAssertions;
using KatalepsisFT.Application.Demo;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Web.Tests.Demo;

public class DemoBookBuilderTests
{
    private static readonly Dictionary<string, decimal> Inception = new() { ["BTC"] = 100m, ["ETH"] = 10m, ["ONDO"] = 0.5m };

    [Fact]
    public void Start_SpotIsPaidFromCapital_PerpsCostNothing_RestIsCash()
    {
        DemoManager manager = new("M", 1_000m, [new("BTC", InstrumentType.Spot, 0.5m), new("BTC", InstrumentType.Perp, -0.5m), new("ETH", InstrumentType.Perp, 0.3m)], []);

        IReadOnlyList<DemoLine> lines = DemoBookBuilder.Start(manager, Inception, 0m);

        lines.Should().BeEquivalentTo(
            [
                new DemoLine("BTC", InstrumentType.Spot, 5m, 100m),
                new DemoLine("BTC", InstrumentType.Perp, -5m, 100m),
                new DemoLine("ETH", InstrumentType.Perp, 30m, 10m),
                new DemoLine("USDC", InstrumentType.Cash, 500m, 1m),
            ]
        );
    }

    [Fact]
    public void Start_OnDayOne_NavEqualsCapital()
    {
        DemoManager manager = new("M", 1_000m, [new("BTC", InstrumentType.Spot, 0.5m), new("ETH", InstrumentType.Perp, -0.4m)], []);

        IReadOnlyList<DemoValuedLine> valued = DemoBookBuilder.Value(DemoBookBuilder.Start(manager, Inception, 0m), Inception);

        valued.Sum(v => v.Value.MarketValueUsd).Should().Be(1_000m);
    }

    [Fact]
    public void Value_HedgedPair_KeepsNavFlatWhenThePriceMoves()
    {
        DemoManager manager = new("M", 1_000m, [new("BTC", InstrumentType.Spot, 0.5m), new("BTC", InstrumentType.Perp, -0.5m)], []);
        IReadOnlyList<DemoLine> lines = DemoBookBuilder.Start(manager, Inception, 0m);

        IReadOnlyList<DemoValuedLine> valued = DemoBookBuilder.Value(lines, new Dictionary<string, decimal> { ["BTC"] = 60m });

        valued.Sum(v => v.Value.MarketValueUsd).Should().Be(1_000m);
        valued.Sum(v => v.Value.ExposureUsd).Should().Be(0m);
    }

    [Fact]
    public void Start_CrowdedAsset_IsSharedOutBySignedShare()
    {
        DemoManager manager = new("M", 1_000_000m, [], [new(InstrumentType.Perp, -0.15m)]);

        DemoLine crowded = DemoBookBuilder.Start(manager, Inception, 1_170_000m).Single(l => l.Asset == DemoFundDefinition.CrowdedAsset);

        // The fund's shares add up to 1.17, so 0.15 of them is 150,000 units, short.
        crowded.Should().Be(new DemoLine("ONDO", InstrumentType.Perp, -150_000m, 0.5m));
    }

    [Fact]
    public void Start_SpotCostingMoreThanCapital_IsRefused()
    {
        DemoManager manager = new("M", 1_000m, [new("BTC", InstrumentType.Spot, 1.2m)], []);

        Action start = () => DemoBookBuilder.Start(manager, Inception, 0m);

        start.Should().Throw<InvalidOperationException>().WithMessage("*negative cash*");
    }

    [Fact]
    public void Definition_EveryManagerNameIsUnique_AndFiveManagersHoldTheCrowdedAsset()
    {
        DemoManager[] managers = DemoFundDefinition.Sleeves.SelectMany(s => s.Managers).ToArray();

        managers.Select(m => m.Name).Should().OnlyHaveUniqueItems();
        managers.Count(m => m.Crowded.Length > 0).Should().Be(5);
        managers.SelectMany(m => m.Crowded).Should().Contain(c => c.Share < 0m, "one manager is on the other side");
    }
}

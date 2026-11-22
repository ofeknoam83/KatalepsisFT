using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Application.Demo;

/// <param name="Weight">Signed share of the manager's starting capital: exposure for a perp, cost for spot.</param>
public sealed record DemoHolding(string Asset, InstrumentType Type, decimal Weight);

/// <param name="Share">Signed share of the fund's total position in the crowded asset.</param>
public sealed record DemoCrowdedHolding(InstrumentType Type, decimal Share);

public sealed record DemoManager(string Name, decimal CapitalUsd, DemoHolding[] Holdings, DemoCrowdedHolding[] Crowded);

public sealed record DemoSleeve(string Name, MandateType MandateType, (MandateLimitKind Kind, decimal Value)[] Limits, DemoManager[] Managers);

/// <summary>
/// The built-in example: one liquid master fund with three sleeves and nine invented managers. Positions are invented and
/// held unchanged from the inception date; prices are real. One asset is deliberately held by five managers, one of
/// them short, and sized against its real traded volume so that crowding and slow exits show up.
/// </summary>
public static class DemoFundDefinition
{
    public const string FundName = "Liquid Master Fund";
    public const string BenchmarkName = "BTC";
    public const string CashAsset = "USDC";
    public const string CrowdedAsset = "ONDO";

    /// <summary>The fund's gross position in the crowded asset, as a multiple of its 30-day average daily volume.</summary>
    public const decimal CrowdedAdvMultiple = 1.6m;
    public const int AdvWindowDays = 30;

    public static readonly DateOnly Inception = new(2026, 1, 1);

    /// <summary>Price history is loaded from here, so a year of returns exists before the fund starts.</summary>
    public static readonly DateOnly HistoryStart = new(2025, 1, 1);

    public static readonly (string Symbol, string Name)[] CryptoAssets =
    [
        ("BTC", "Bitcoin"),
        ("ETH", "Ethereum"),
        ("SOL", "Solana"),
        ("XRP", "XRP"),
        ("LINK", "Chainlink"),
        ("AVAX", "Avalanche"),
        ("DOGE", "Dogecoin"),
        ("AAVE", "Aave"),
        ("ONDO", "Ondo"),
    ];

    private const InstrumentType Spot = InstrumentType.Spot;
    private const InstrumentType Perp = InstrumentType.Perp;

    public static readonly DemoSleeve[] Sleeves =
    [
        new(
            "Beta",
            MandateType.Beta,
            [(MandateLimitKind.SingleAssetMax, 0.60m), (MandateLimitKind.ManagerDrawdownMin, -0.40m)],
            [
                new("Northgate Index", 60_000_000m, [new("BTC", Spot, 0.55m), new("ETH", Spot, 0.22m), new("SOL", Spot, 0.08m), new("XRP", Spot, 0.035m), new("LINK", Spot, 0.03m), new("AVAX", Spot, 0.025m)], []),
                new("Halyard Core", 40_000_000m, [new("BTC", Spot, 0.50m), new("ETH", Spot, 0.20m)], []),
            ]
        ),
        new(
            "Market Neutral",
            MandateType.MarketNeutral,
            [(MandateLimitKind.NetExposureMin, -0.05m), (MandateLimitKind.NetExposureMax, 0.05m), (MandateLimitKind.SingleAssetMax, 1.20m)],
            [
                new("Tessera Basis", 35_000_000m, [new("BTC", Spot, 0.50m), new("BTC", Perp, -0.50m), new("ETH", Spot, 0.25m), new("ETH", Perp, -0.25m), new("SOL", Spot, 0.12m), new("SOL", Perp, -0.12m)], []),
                new("Quorum Funding", 25_000_000m, [new("XRP", Spot, 0.20m), new("XRP", Perp, -0.20m), new("DOGE", Spot, 0.10m), new("DOGE", Perp, -0.10m)], [new(Spot, 0.19m), new(Perp, -0.19m)]),
                new("Vireo Vol", 20_000_000m, [new("BTC", Spot, 0.10m), new("BTC", Perp, -0.10m)], []),
            ]
        ),
        new(
            "Directional",
            MandateType.Directional,
            [(MandateLimitKind.GrossExposureMax, 2.00m), (MandateLimitKind.SingleAssetMax, 0.15m), (MandateLimitKind.ManagerDrawdownMin, -0.25m), (MandateLimitKind.DaysToLiquidateMax, 5m)],
            [
                new("Ardent L/S", 22_000_000m, [new("SOL", Spot, 0.28m), new("XRP", Spot, 0.16m), new("ETH", Perp, -0.22m), new("AVAX", Perp, -0.11m), new("DOGE", Perp, -0.07m)], [new(Spot, 0.26m)]),
                new("Kestrel Momentum", 20_000_000m, [new("BTC", Spot, 0.25m), new("AAVE", Spot, 0.12m), new("SOL", Perp, 0.15m), new("XRP", Perp, 0.25m), new("ETH", Perp, 0.20m)], [new(Perp, 0.22m)]),
                new("Blackfen Alpha", 16_000_000m, [new("LINK", Spot, 0.18m), new("AAVE", Spot, 0.14m)], [new(Perp, -0.15m)]),
                new("Solstice Event", 12_000_000m, [new("ETH", Spot, 0.17m), new("BTC", Perp, 0.12m)], [new(Spot, 0.16m)]),
            ]
        ),
    ];

    /// <summary>Sum of the absolute crowded shares across the fund; each manager gets its share of this.</summary>
    public static decimal TotalCrowdedShare => Sleeves.SelectMany(s => s.Managers).SelectMany(m => m.Crowded).Sum(c => Math.Abs(c.Share));
}

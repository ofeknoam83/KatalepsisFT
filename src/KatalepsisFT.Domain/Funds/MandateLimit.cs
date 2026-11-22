namespace KatalepsisFT.Domain.Funds;

public enum MandateLimitKind
{
    /// <summary>Largest allowed gross exposure as a fraction of sleeve NAV (2.0 = 200%).</summary>
    GrossExposureMax = 1,

    /// <summary>Lowest allowed net exposure as a fraction of sleeve NAV.</summary>
    NetExposureMin = 2,

    /// <summary>Highest allowed net exposure as a fraction of sleeve NAV.</summary>
    NetExposureMax = 3,

    /// <summary>Largest allowed gross exposure to one asset as a fraction of sleeve NAV.</summary>
    SingleAssetMax = 4,

    /// <summary>Deepest allowed drawdown of one manager, negative (−0.25 = 25% below the peak).</summary>
    ManagerDrawdownMin = 5,

    /// <summary>Longest allowed time to liquidate one asset, in days.</summary>
    DaysToLiquidateMax = 6,
}

/// <summary>One limit of a sleeve's mandate. Each drives a mandate check and an alert rule.</summary>
public class MandateLimit
{
    public int Id { get; set; }
    public int SleeveId { get; set; }
    public Sleeve Sleeve { get; set; } = null!;
    public MandateLimitKind Kind { get; set; }
    public decimal Value { get; set; }
}

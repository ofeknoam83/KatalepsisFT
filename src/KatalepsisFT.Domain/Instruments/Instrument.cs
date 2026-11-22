namespace KatalepsisFT.Domain.Instruments;

public enum InstrumentType
{
    Spot = 1,
    Perp = 2,
    Future = 3,
    Option = 4,
    Equity = 5,
    Cash = 6,
}

public enum OptionRight
{
    Call = 1,
    Put = 2,
}

/// <summary>What is actually held. A CME BTC future and BTC spot are two instruments on the same asset.</summary>
public class Instrument
{
    public int Id { get; set; }

    /// <summary>The underlying asset.</summary>
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public InstrumentType Type { get; set; }

    /// <summary>Display name, unique: "BTC", "BTC-PERP", "BTC Dec-26 future", "ETH 26-Dec-26 4000 call".</summary>
    public required string Symbol { get; set; }

    /// <summary>Units of the underlying per contract; 1 for spot, perps quoted in the asset, and equities.</summary>
    public decimal Multiplier { get; set; } = 1m;
    public DateOnly? Expiry { get; set; }
    public decimal? Strike { get; set; }
    public OptionRight? OptionRight { get; set; }

    /// <summary>Perps, futures and options are derivatives: their exposure is not their market value.</summary>
    public bool IsDerivative => Type is InstrumentType.Perp or InstrumentType.Future or InstrumentType.Option;
}

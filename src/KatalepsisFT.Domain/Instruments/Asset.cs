namespace KatalepsisFT.Domain.Instruments;

public enum AssetClass
{
    Crypto = 1,
    Equity = 2,
    Cash = 3,
}

/// <summary>What exposure is measured in: BTC, ETH, COIN. Positions in different instruments on the same asset roll up here.</summary>
public class Asset
{
    public int Id { get; set; }

    /// <summary>Upper-case ticker, unique.</summary>
    public required string Symbol { get; set; }
    public required string Name { get; set; }
    public AssetClass AssetClass { get; set; }
}

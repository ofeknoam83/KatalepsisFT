namespace KatalepsisFT.Domain.Instruments;

/// <summary>Daily close and traded volume of an asset.</summary>
public class PriceBar
{
    public long Id { get; set; }
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public DateOnly Date { get; set; }
    public decimal Close { get; set; }
    public decimal VolumeUsd { get; set; }

    /// <summary>Where the bar came from, for example "binance" or "demo".</summary>
    public required string Source { get; set; }
}

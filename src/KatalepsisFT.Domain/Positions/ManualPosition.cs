using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Domain.Positions;

/// <summary>
/// A position entered by hand or uploaded: anything no connection can read, such as equities, an unsupported venue or
/// an over-the-counter deal. It stays in the manager's book until someone changes or removes it.
/// </summary>
public class ManualPosition
{
    public int Id { get; set; }
    public int ManagerAccountId { get; set; }
    public ManagerAccount ManagerAccount { get; set; } = null!;
    public InstrumentType Type { get; set; }

    /// <summary>Upper-case ticker of the underlying asset.</summary>
    public required string Asset { get; set; }

    /// <summary>Signed: negative is short.</summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// USD per unit, entered by hand. When empty the latest daily close of the asset is used, which only exists for
    /// assets with a price feed. For an option this is the premium.
    /// </summary>
    public decimal? Price { get; set; }
    public decimal? EntryPrice { get; set; }
    public DateOnly? Expiry { get; set; }
    public decimal? Strike { get; set; }
    public OptionRight? OptionRight { get; set; }

    /// <summary>Option delta per unit, between −1 and 1.</summary>
    public decimal? Delta { get; set; }

    /// <summary>Price of an option's underlying, when it has no price feed.</summary>
    public decimal? UnderlyingPrice { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

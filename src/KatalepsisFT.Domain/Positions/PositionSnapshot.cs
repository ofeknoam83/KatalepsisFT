using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Domain.Positions;

/// <summary>One manager's position in one instrument at the end of one day, already valued.</summary>
public class PositionSnapshot
{
    public long Id { get; set; }
    public int ManagerAccountId { get; set; }
    public ManagerAccount ManagerAccount { get; set; } = null!;
    public int InstrumentId { get; set; }
    public Instrument Instrument { get; set; } = null!;
    public DateOnly Date { get; set; }

    /// <summary>Signed: negative is short.</summary>
    public decimal Quantity { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal MarkPrice { get; set; }

    /// <summary>Price of the underlying asset; equals <see cref="MarkPrice"/> except for options.</summary>
    public decimal UnderlyingPrice { get; set; }

    /// <summary>Option delta per unit; null for everything else.</summary>
    public decimal? Delta { get; set; }

    /// <summary>What the position adds to NAV.</summary>
    public decimal MarketValueUsd { get; set; }

    /// <summary>Signed, delta-adjusted exposure to the underlying asset. Zero for cash.</summary>
    public decimal ExposureUsd { get; set; }
    public decimal UnrealizedPnlUsd { get; set; }
}

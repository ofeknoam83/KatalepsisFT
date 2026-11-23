using KatalepsisFT.Domain.Funds;

namespace KatalepsisFT.Domain.Positions;

public enum CashFlowKind
{
    Subscription = 1,
    Redemption = 2,

    /// <summary>Capital moved between managers or sleeves of the same fund; not performance at fund level.</summary>
    Transfer = 3,
}

/// <summary>Capital moved into or out of a manager's book. Needed so that it is not counted as performance.</summary>
public class CashFlow
{
    public long Id { get; set; }
    public int ManagerAccountId { get; set; }
    public ManagerAccount ManagerAccount { get; set; } = null!;
    public DateOnly Date { get; set; }
    public CashFlowKind Kind { get; set; }

    /// <summary>Signed: positive is money in.</summary>
    public decimal AmountUsd { get; set; }
    public string? Note { get; set; }

    /// <summary>Set on flows read from a venue: venue, connection and the venue's own identifier. Unique per manager.</summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// True when the system recorded the flow because an account entered or left the manager's book (a key added,
    /// switched off, a manager retired), as opposed to money a venue or a person reported.
    /// </summary>
    public bool IsBookChange { get; set; }
}

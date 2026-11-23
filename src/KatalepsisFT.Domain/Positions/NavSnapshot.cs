using KatalepsisFT.Domain.Funds;

namespace KatalepsisFT.Domain.Positions;

/// <summary>A manager's net asset value at the end of one day. Sleeve and fund NAV are sums of these.</summary>
public class NavSnapshot
{
    public long Id { get; set; }
    public int ManagerAccountId { get; set; }
    public ManagerAccount ManagerAccount { get; set; } = null!;
    public DateOnly Date { get; set; }
    public decimal NavUsd { get; set; }

    /// <summary>True for a day taken from an imported track record rather than read from the manager's accounts or positions.</summary>
    public bool IsImported { get; set; }
}

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
}

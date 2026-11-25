using KatalepsisFT.Domain.Funds;

namespace KatalepsisFT.Domain.Positions;

/// <summary>A manager's NAV and exposure at a moment within the day, kept about every ten seconds for a day and one per five minutes for ninety days.</summary>
public class NavSample
{
    public long Id { get; set; }
    public int ManagerAccountId { get; set; }
    public ManagerAccount ManagerAccount { get; set; } = null!;
    public DateTimeOffset At { get; set; }
    public decimal NavUsd { get; set; }
    public decimal GrossUsd { get; set; }
    public decimal NetUsd { get; set; }
}

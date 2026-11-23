using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Live;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Connections;

/// <summary>
/// Everything that follows from a manager's book having changed: today's positions and NAV are rewritten, an intraday
/// sample is kept, the fund's alert rules are checked, and whoever is looking at the fund is told.
/// </summary>
public sealed class ManagerBookService(IFtDbContext database, ManagerBookWriter writer, AlertEvaluator alerts, IntradayService intraday, IFundChangeNotifier notifier, TimeProvider clock)
{
    public async Task RefreshAsync(int managerAccountId, CancellationToken cancellationToken)
    {
        (decimal nav, decimal gross, decimal net) = await writer.WriteAsync(managerAccountId, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), cancellationToken);
        await intraday.RecordAsync(managerAccountId, nav, gross, net, cancellationToken);
        int fundId = await database.ManagerAccounts.Where(m => m.Id == managerAccountId).Select(m => m.Sleeve.FundId).SingleAsync(cancellationToken);
        await alerts.EvaluateAsync(fundId, cancellationToken);
        await notifier.NotifyAsync(fundId, cancellationToken);
    }

    /// <summary>
    /// Managers whose book is made of hand-entered positions only. Nothing reads them from a venue, so they are
    /// revalued on a timer: prices move, and each new day needs its own record.
    /// </summary>
    public async Task<int> RefreshManualOnlyAsync(CancellationToken cancellationToken)
    {
        List<int> managerIds = await database
            .ManualPositions.Select(p => p.ManagerAccountId)
            .Distinct()
            .Where(id => !database.VenueConnections.Any(c => c.ManagerAccountId == id && c.State != Domain.Connections.ConnectionState.Disabled))
            .ToListAsync(cancellationToken);
        foreach (int id in managerIds)
        {
            await RefreshAsync(id, cancellationToken);
        }

        return managerIds.Count;
    }
}

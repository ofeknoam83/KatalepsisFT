using KatalepsisFT.Analytics.Performance;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Performance;

/// <summary>NAV summed over a set of managers per day, with the money that moved in or out.</summary>
public sealed record NavSeries(List<DateOnly> Dates, List<decimal> Navs, List<decimal> Flows)
{
    /// <summary>One return per day after the first.</summary>
    public double[] Returns() => ReturnCalculator.DailyReturns(Navs, Flows);
}

/// <summary>Daily NAV and cash flows of a fund's managers, loaded once and then summed for any group of them.</summary>
public sealed class NavHistory
{
    private readonly Dictionary<int, SortedDictionary<DateOnly, decimal>> _navs;
    private readonly Dictionary<int, Dictionary<DateOnly, decimal>> _flows;

    private NavHistory(Dictionary<int, SortedDictionary<DateOnly, decimal>> navs, Dictionary<int, Dictionary<DateOnly, decimal>> flows)
    {
        _navs = navs;
        _flows = flows;
    }

    public static async Task<NavHistory> LoadAsync(IFtDbContext database, int[] managerIds, CancellationToken cancellationToken)
    {
        var navRows = await database
            .NavSnapshots.AsNoTracking()
            .Where(n => managerIds.Contains(n.ManagerAccountId))
            .Select(n => new { n.ManagerAccountId, n.Date, n.NavUsd })
            .ToListAsync(cancellationToken);
        var flowRows = await database
            .CashFlows.AsNoTracking()
            .Where(f => managerIds.Contains(f.ManagerAccountId))
            .Select(f => new { f.ManagerAccountId, f.Date, f.AmountUsd })
            .ToListAsync(cancellationToken);
        return new NavHistory(
            navRows.GroupBy(n => n.ManagerAccountId).ToDictionary(g => g.Key, g => new SortedDictionary<DateOnly, decimal>(g.ToDictionary(n => n.Date, n => n.NavUsd))),
            flowRows.GroupBy(f => f.ManagerAccountId).ToDictionary(g => g.Key, g => g.GroupBy(f => f.Date).ToDictionary(d => d.Key, d => d.Sum(f => f.AmountUsd)))
        );
    }

    /// <summary>The managers among <paramref name="managerIds"/> that have been valued at least once.</summary>
    public int[] Valued(IEnumerable<int> managerIds) => managerIds.Where(_navs.ContainsKey).ToArray();

    /// <summary>The last day on which every one of the managers has been valued. A fund never mixes days.</summary>
    public DateOnly AsOf(int[] managerIds) => managerIds.Min(m => _navs[m].Keys.Max());

    /// <summary>
    /// Sums the managers' NAV per day. A manager that appears after the series began brings its first NAV in as a flow,
    /// so its arrival is not counted as a gain. A manager with a missing day keeps its last NAV for that day.
    /// </summary>
    public NavSeries Series(int[] managerIds, DateOnly asOf)
    {
        DateOnly first = managerIds.Min(m => _navs[m].Keys.Min());
        List<DateOnly> dates = [];
        List<decimal> totals = [];
        List<decimal> netFlows = [];
        Dictionary<int, decimal> last = [];
        for (DateOnly day = first; day <= asOf; day = day.AddDays(1))
        {
            decimal flow = 0m;
            foreach (int manager in managerIds)
            {
                bool known = last.ContainsKey(manager);
                if (_navs[manager].TryGetValue(day, out decimal nav))
                {
                    if (!known && day > first)
                    {
                        flow += nav;
                    }

                    last[manager] = nav;
                }

                if (known && _flows.TryGetValue(manager, out Dictionary<DateOnly, decimal>? byDay) && byDay.TryGetValue(day, out decimal amount))
                {
                    flow += amount;
                }
            }

            dates.Add(day);
            totals.Add(last.Values.Sum());
            netFlows.Add(flow);
        }

        return new NavSeries(dates, totals, netFlows);
    }
}

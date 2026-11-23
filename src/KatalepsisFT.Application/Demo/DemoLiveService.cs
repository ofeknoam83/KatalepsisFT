using KatalepsisFT.Analytics.Valuation;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Live;
using KatalepsisFT.Application.Prices;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Demo;

/// <summary>
/// Keeps the demo fund moving. Its managers hold the same quantities they always did; this values them at the prices
/// of the moment and writes the result as today's book, the way a venue read does for a real manager. When the UTC
/// day changes, the last valuation of the day stays as that day's record.
/// </summary>
public sealed class DemoLiveService(IFtDbContext database, ILivePriceSource prices, IntradayService intraday, AlertEvaluator alerts, IFundChangeNotifier notifier, TimeProvider clock)
{
    /// <param name="full">Also keep intraday samples and check the fund's alert rules; the rest is done on every call.</param>
    /// <returns>False when there is no demo fund, or it has never been valued.</returns>
    public async Task<bool> RevalueAsync(bool full, CancellationToken cancellationToken)
    {
        Fund? fund = await database.Funds.AsNoTracking().FirstOrDefaultAsync(f => f.IsDemo, cancellationToken);
        if (fund is null)
        {
            return false;
        }

        Dictionary<int, DateOnly> lastDay = await database
            .NavSnapshots.Where(n => n.ManagerAccount.Sleeve.FundId == fund.Id)
            .GroupBy(n => n.ManagerAccountId)
            .Select(g => new { ManagerAccountId = g.Key, Date = g.Max(n => n.Date) })
            .ToDictionaryAsync(x => x.ManagerAccountId, x => x.Date, cancellationToken);
        if (lastDay.Count == 0)
        {
            return false;
        }

        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        List<DateOnly> days = lastDay.Values.Distinct().ToList();
        List<PositionSnapshot> rows = (
            await database.PositionSnapshots.Include(p => p.Instrument).ThenInclude(i => i.Asset).Where(p => days.Contains(p.Date) && p.ManagerAccount.Sleeve.FundId == fund.Id).ToListAsync(cancellationToken)
        )
            .Where(p => lastDay[p.ManagerAccountId] == p.Date)
            .ToList();
        string[] assets = rows.Where(p => p.Instrument.Type != InstrumentType.Cash).Select(p => p.Instrument.Asset.Symbol).Distinct().ToArray();
        IReadOnlyDictionary<string, decimal> live = await prices.GetPricesAsync(assets, cancellationToken);

        Dictionary<int, (decimal Nav, decimal Gross, decimal Net)> books = [];
        foreach (IGrouping<int, PositionSnapshot> book in rows.GroupBy(p => p.ManagerAccountId))
        {
            decimal nav = 0m,
                gross = 0m,
                net = 0m;
            foreach (PositionSnapshot held in book)
            {
                // An asset the price source does not answer for keeps its last price: the book must not lose a line.
                decimal mark = held.Instrument.Type == InstrumentType.Cash ? 1m : live.TryGetValue(held.Instrument.Asset.Symbol, out decimal price) ? Math.Round(price, 8) : held.MarkPrice;
                PositionValue value = PositionValuer.Value(new PositionInput(held.Instrument.Type, held.Quantity, mark, EntryPrice: held.EntryPrice));
                // Today's rows are changed where they stand; an earlier day's rows are left as that day's record.
                PositionSnapshot row = held.Date == today ? held : database.PositionSnapshots.Add(new PositionSnapshot { ManagerAccountId = held.ManagerAccountId, InstrumentId = held.InstrumentId, Date = today, Quantity = held.Quantity, EntryPrice = held.EntryPrice }).Entity;
                row.MarkPrice = mark;
                row.UnderlyingPrice = mark;
                row.MarketValueUsd = value.MarketValueUsd;
                row.ExposureUsd = value.ExposureUsd;
                row.UnrealizedPnlUsd = value.UnrealizedPnlUsd;
                nav += value.MarketValueUsd;
                gross += Math.Abs(value.ExposureUsd);
                net += value.ExposureUsd;
            }

            books[book.Key] = (nav, gross, net);
        }

        Dictionary<int, NavSnapshot> todays = await database.NavSnapshots.Where(n => n.Date == today && n.ManagerAccount.Sleeve.FundId == fund.Id).ToDictionaryAsync(n => n.ManagerAccountId, cancellationToken);
        foreach ((int managerId, (decimal nav, _, _)) in books)
        {
            if (todays.TryGetValue(managerId, out NavSnapshot? existing))
            {
                existing.NavUsd = nav;
            }
            else
            {
                database.NavSnapshots.Add(new NavSnapshot { ManagerAccountId = managerId, Date = today, NavUsd = nav });
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        if (full)
        {
            foreach ((int managerId, (decimal nav, decimal gross, decimal net)) in books)
            {
                await intraday.RecordAsync(managerId, nav, gross, net, cancellationToken);
            }

            await alerts.EvaluateAsync(fund.Id, cancellationToken);
        }

        await notifier.NotifyAsync(fund.Id, cancellationToken);
        return true;
    }
}

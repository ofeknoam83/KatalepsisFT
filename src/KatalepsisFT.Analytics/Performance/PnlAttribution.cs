namespace KatalepsisFT.Analytics.Performance;

/// <summary>One position on one day, as far as attribution needs it.</summary>
public sealed record AttributionRow(int PositionKey, DateOnly Date, decimal Quantity, decimal Multiplier, decimal MarkPrice, bool IsCash);

/// <summary>
/// Splits a period's profit and loss by position: each day a position earns the quantity held at the previous close
/// times the price move of the day. Cash earns nothing. Profit made by trading during a day is not captured here; what
/// this explains and what NAV actually did are shown side by side so that the difference is visible.
/// </summary>
public static class PnlAttribution
{
    /// <returns>Profit and loss in USD per position key, over the days after the first day of each position.</returns>
    public static IReadOnlyDictionary<int, decimal> ByPosition(IEnumerable<AttributionRow> rows)
    {
        Dictionary<int, decimal> pnl = [];
        foreach (IGrouping<int, AttributionRow> position in rows.Where(r => !r.IsCash).GroupBy(r => r.PositionKey))
        {
            AttributionRow? previous = null;
            decimal total = 0m;
            foreach (AttributionRow row in position.OrderBy(r => r.Date))
            {
                // Only consecutive days: after a gap the position was closed and reopened, and the move in between is not its PnL.
                if (previous is not null && row.Date.DayNumber - previous.Date.DayNumber == 1)
                {
                    total += previous.Quantity * previous.Multiplier * (row.MarkPrice - previous.MarkPrice);
                }

                previous = row;
            }

            pnl[position.Key] = total;
        }

        return pnl;
    }
}

namespace KatalepsisFT.Analytics.Performance;

/// <summary>
/// Time-weighted returns from a daily NAV series and the money that moved in or out.
/// The daily return is the one Katalepsis uses (Application/Services/Balance/BalanceHistoryService.cs:103-119):
/// r = NAV / (previous NAV + deposits) − 1, that is, money coming in is taken to arrive before the day's move.
/// Two things differ from that service. Money going out is taken to leave after the day's move,
/// r = (NAV + withdrawals) / previous NAV − 1, because what leaves is sized at what the book was worth when it left.
/// And returns are compounded unrounded.
/// </summary>
public static class ReturnCalculator
{
    /// <param name="navs">NAV at each close; the first element is the starting NAV.</param>
    /// <param name="netFlows">Net money in (+) or out (−) on each day, same length as <paramref name="navs"/>; the first element is ignored.</param>
    /// <returns>One return per day after the first. A day that starts with nothing invested returns 0.</returns>
    public static double[] DailyReturns(IReadOnlyList<decimal> navs, IReadOnlyList<decimal>? netFlows = null)
    {
        if (netFlows is not null && netFlows.Count != navs.Count)
        {
            throw new ArgumentException("There must be one flow per NAV.", nameof(netFlows));
        }

        double[] returns = new double[Math.Max(0, navs.Count - 1)];
        for (int t = 1; t < navs.Count; t++)
        {
            decimal flow = netFlows?[t] ?? 0m;
            if (flow < 0m)
            {
                // Money taken out is sized at what the book was worth when it left, so it is counted as leaving at the
                // end of the day: the day's return is earned on what was there in the morning. Counted as leaving in
                // the morning, a manager retired at 98 after starting the day at 100 would show a return of -100%.
                returns[t - 1] = navs[t - 1] > 0m ? (double)((navs[t] - flow) / navs[t - 1]) - 1.0 : 0.0;
                continue;
            }

            decimal invested = navs[t - 1] + flow;
            returns[t - 1] = invested > 0m ? (double)(navs[t] / invested) - 1.0 : 0.0;
        }

        return returns;
    }

    /// <summary>Growth of 1: the first element is 1, each next one is the previous times (1 + return).</summary>
    public static decimal[] GrowthIndex(IReadOnlyList<double> returns)
    {
        decimal[] index = new decimal[returns.Count + 1];
        double level = 1.0;
        index[0] = 1m;
        for (int t = 0; t < returns.Count; t++)
        {
            level *= 1.0 + returns[t];
            index[t + 1] = (decimal)level;
        }

        return index;
    }

    /// <summary>Compounded return of a run of daily returns; 0 for an empty run.</summary>
    public static double Compound(IEnumerable<double> returns)
    {
        double level = 1.0;
        foreach (double r in returns)
        {
            level *= 1.0 + r;
        }

        return level - 1.0;
    }
}

namespace KatalepsisFT.Analytics.Performance;

/// <summary>
/// Time-weighted returns from a daily NAV series and the money that moved in or out.
/// The daily return is the one Katalepsis uses (Application/Services/Balance/BalanceHistoryService.cs:103-119):
/// r = NAV / (previous NAV + deposits − withdrawals) − 1, that is, a flow is taken to arrive before the day's move.
/// Unlike that service, returns are compounded unrounded.
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
            decimal invested = navs[t - 1] + (netFlows?[t] ?? 0m);
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

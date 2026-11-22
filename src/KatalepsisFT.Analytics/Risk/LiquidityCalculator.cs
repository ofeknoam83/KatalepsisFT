namespace KatalepsisFT.Analytics.Risk;

/// <param name="GrossUsd">Long plus short, both counted: closing either side needs volume.</param>
/// <param name="AverageDailyVolumeUsd">Null when the volume is unknown.</param>
public sealed record LiquidityInput(string Asset, decimal GrossUsd, decimal? AverageDailyVolumeUsd);

/// <param name="Days">Null when the volume is unknown or zero: the position cannot be timed.</param>
public sealed record LiquidityLine(string Asset, decimal GrossUsd, decimal? AverageDailyVolumeUsd, decimal? DailyCapacityUsd, double? Days);

/// <summary>How much of a book can be closed within 1, 5 and 20 days, and how much takes longer or cannot be timed.</summary>
public sealed record LiquidityLadder(decimal WithinOneDayUsd, decimal OneToFiveDaysUsd, decimal FiveToTwentyDaysUsd, decimal BeyondTwentyDaysUsd, decimal UnknownUsd)
{
    public decimal TotalUsd => WithinOneDayUsd + OneToFiveDaysUsd + FiveToTwentyDaysUsd + BeyondTwentyDaysUsd + UnknownUsd;
}

/// <summary>
/// Time to close a position when trading no more than a set share of the asset's average daily volume. Positions of
/// several managers in the same asset compete for the same volume, so a fund is timed on its combined position.
/// </summary>
public static class LiquidityCalculator
{
    public static IReadOnlyList<LiquidityLine> Lines(IEnumerable<LiquidityInput> positions, decimal participation)
    {
        if (participation <= 0m || participation > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(participation), participation, "Participation is a share of daily volume between 0 and 1.");
        }

        return positions
            .Where(p => p.GrossUsd > 0m)
            .Select(p =>
            {
                decimal? capacity = p.AverageDailyVolumeUsd is > 0m ? p.AverageDailyVolumeUsd.Value * participation : null;
                return new LiquidityLine(p.Asset, p.GrossUsd, p.AverageDailyVolumeUsd, capacity, capacity is null ? null : (double)(p.GrossUsd / capacity.Value));
            })
            .OrderByDescending(l => l.Days ?? double.MaxValue)
            .ThenByDescending(l => l.GrossUsd)
            .ToList();
    }

    /// <param name="cashUsd">Cash is available at once.</param>
    public static LiquidityLadder Ladder(IEnumerable<LiquidityLine> lines, decimal cashUsd)
    {
        decimal one = Math.Max(0m, cashUsd), five = 0m, twenty = 0m, beyond = 0m, unknown = 0m;
        foreach (LiquidityLine line in lines)
        {
            if (line.DailyCapacityUsd is not decimal capacity)
            {
                unknown += line.GrossUsd;
                continue;
            }

            decimal withinOne = Math.Min(line.GrossUsd, capacity);
            decimal withinFive = Math.Min(line.GrossUsd, 5m * capacity);
            decimal withinTwenty = Math.Min(line.GrossUsd, 20m * capacity);
            one += withinOne;
            five += withinFive - withinOne;
            twenty += withinTwenty - withinFive;
            beyond += line.GrossUsd - withinTwenty;
        }

        return new LiquidityLadder(one, five, twenty, beyond, unknown);
    }
}

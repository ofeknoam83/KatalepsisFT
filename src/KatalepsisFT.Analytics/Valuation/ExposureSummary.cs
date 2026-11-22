namespace KatalepsisFT.Analytics.Valuation;

/// <summary>Long, short, gross and net of a set of signed exposures.</summary>
public sealed record ExposureSummary(decimal LongUsd, decimal ShortUsd)
{
    public decimal GrossUsd => LongUsd - ShortUsd;
    public decimal NetUsd => LongUsd + ShortUsd;

    /// <summary>Short exposure stays negative, so two managers on opposite sides add to gross and cancel in net.</summary>
    public static ExposureSummary Of(IEnumerable<decimal> signedExposures)
    {
        decimal longs = 0m, shorts = 0m;
        foreach (decimal exposure in signedExposures)
        {
            if (exposure > 0m)
            {
                longs += exposure;
            }
            else
            {
                shorts += exposure;
            }
        }

        return new ExposureSummary(longs, shorts);
    }
}

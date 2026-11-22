namespace KatalepsisFT.Application.Prices;

public sealed record DailyBar(DateOnly Date, decimal Close, decimal VolumeUsd);

/// <summary>A source of completed daily bars for an asset.</summary>
public interface IDailyPriceSource
{
    /// <summary>Short name stored with each bar, for example "binance".</summary>
    string Name { get; }

    /// <summary>Completed days between the two dates, inclusive, oldest first; null when the source does not know the asset.</summary>
    Task<IReadOnlyList<DailyBar>?> GetDailyBarsAsync(string assetSymbol, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

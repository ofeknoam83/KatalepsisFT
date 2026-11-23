namespace KatalepsisFT.Application.Prices;

/// <summary>Prices as they are right now, as opposed to the completed daily bars of <see cref="IDailyPriceSource"/>.</summary>
public interface ILivePriceSource
{
    /// <summary>USD per unit of each asset the source knows; an asset it does not list is simply absent.</summary>
    Task<IReadOnlyDictionary<string, decimal>> GetPricesAsync(IReadOnlyCollection<string> assetSymbols, CancellationToken cancellationToken);
}

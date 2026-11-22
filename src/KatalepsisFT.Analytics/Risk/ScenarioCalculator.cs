namespace KatalepsisFT.Analytics.Risk;

/// <param name="Beta">How far the asset moves when the reference asset moves by one, from its price history.</param>
public sealed record AssetExposureInput(string Asset, decimal ExposureUsd, double Beta);

/// <summary>
/// Profit or loss of a book if the reference asset (BTC) moves by a given amount and every other asset follows through
/// its beta. Linear: exposure × move. Options would need gamma on top; there are none to reprice yet.
/// </summary>
public static class ScenarioCalculator
{
    /// <param name="referenceMove">Move of the reference asset: −0.2 is a 20% fall.</param>
    /// <param name="betaMultiplier">1 uses each asset's own beta; 1.5 assumes the others move one and a half times as far as history suggests.</param>
    public static decimal Pnl(IEnumerable<AssetExposureInput> exposures, string referenceAsset, double referenceMove, double betaMultiplier)
    {
        decimal total = 0m;
        foreach (AssetExposureInput exposure in exposures)
        {
            double move = string.Equals(exposure.Asset, referenceAsset, StringComparison.OrdinalIgnoreCase) ? referenceMove : referenceMove * exposure.Beta * betaMultiplier;
            // An asset cannot lose more than everything.
            total += exposure.ExposureUsd * (decimal)Math.Max(move, -1.0);
        }

        return Math.Round(total, 2);
    }
}

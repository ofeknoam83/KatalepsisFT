using KatalepsisFT.Domain.Funds;

namespace KatalepsisFT.Analytics.Risk;

/// <summary>What a sleeve currently looks like, as far as its mandate is concerned. Fractions of sleeve NAV.</summary>
/// <param name="WorstManagerDrawdown">The deepest current drawdown among its managers, negative; null when unknown.</param>
/// <param name="SlowestDaysToLiquidate">Days to close its slowest asset; null when no position can be timed.</param>
public sealed record MandateState(
    decimal GrossOverNav,
    decimal NetOverNav,
    string? LargestAsset,
    decimal LargestAssetOverNav,
    string? WorstManager,
    double? WorstManagerDrawdown,
    string? SlowestAsset,
    double? SlowestDaysToLiquidate
);

/// <param name="Current">Null when the figure the rule needs is not available; the check is then neither passed nor failed.</param>
public sealed record MandateCheck(MandateLimitKind Kind, string Rule, decimal? Current, decimal Limit, bool? WithinLimit);

/// <summary>Compares a sleeve with each of its limits.</summary>
public static class MandateChecker
{
    public static IReadOnlyList<MandateCheck> Check(IEnumerable<(MandateLimitKind Kind, decimal Value)> limits, MandateState state) =>
        limits.OrderBy(l => l.Kind).Select(limit => Check(limit.Kind, limit.Value, state)).ToList();

    private static MandateCheck Check(MandateLimitKind kind, decimal limit, MandateState state)
    {
        switch (kind)
        {
            case MandateLimitKind.GrossExposureMax:
                return new MandateCheck(kind, "Gross exposure ÷ NAV", state.GrossOverNav, limit, state.GrossOverNav <= limit);
            case MandateLimitKind.NetExposureMin:
                return new MandateCheck(kind, "Net exposure ÷ NAV, at least", state.NetOverNav, limit, state.NetOverNav >= limit);
            case MandateLimitKind.NetExposureMax:
                return new MandateCheck(kind, "Net exposure ÷ NAV, at most", state.NetOverNav, limit, state.NetOverNav <= limit);
            case MandateLimitKind.SingleAssetMax:
                return new MandateCheck(kind, Named("Largest single asset", state.LargestAsset), state.LargestAssetOverNav, limit, state.LargestAssetOverNav <= limit);
            case MandateLimitKind.ManagerDrawdownMin:
            {
                decimal? current = state.WorstManagerDrawdown is double drawdown ? Math.Round((decimal)drawdown, 6) : null;
                return new MandateCheck(kind, Named("Deepest manager drawdown", state.WorstManager), current, limit, current is null ? null : current >= limit);
            }

            case MandateLimitKind.DaysToLiquidateMax:
            {
                decimal? current = state.SlowestDaysToLiquidate is double days ? Math.Round((decimal)days, 2) : null;
                return new MandateCheck(kind, Named("Slowest asset, days to liquidate", state.SlowestAsset), current, limit, current is null ? null : current <= limit);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown mandate limit.");
        }
    }

    private static string Named(string rule, string? name) => name is null ? rule : $"{rule} ({name})";
}

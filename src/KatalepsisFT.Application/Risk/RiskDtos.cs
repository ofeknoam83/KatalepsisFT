using KatalepsisFT.Application.Holdings;
using KatalepsisFT.Application.Performance;
using KatalepsisFT.Domain.Funds;

namespace KatalepsisFT.Application.Risk;

public sealed record AssetBetaDto(string Asset, decimal NetUsd, decimal GrossUsd, double Beta, bool BetaIsAssumed);

/// <param name="Cells">One row per shock, one column per beta multiplier: profit or loss in USD.</param>
public sealed record ScenarioGridDto(string ReferenceAsset, IReadOnlyList<double> Shocks, IReadOnlyList<double> BetaMultipliers, IReadOnlyList<IReadOnlyList<decimal>> Cells);

/// <param name="Pnl">One value per shock, at each asset's own beta.</param>
public sealed record ScenarioRowDto(PerformanceScope Kind, int Id, string Name, decimal NavUsd, IReadOnlyList<decimal> Pnl);

public sealed record VarDto(decimal ValueAtRiskUsd, decimal ExpectedShortfallUsd, int Days, double Confidence);

public sealed record MandateCheckDto(int SleeveId, string Sleeve, MandateLimitKind Kind, string Rule, decimal? Current, decimal Limit, bool? WithinLimit);

public sealed record VolatilityRowDto(PerformanceScope Kind, int Id, string Name, double? Days30, double? Days90, double? ShareOfVariance);

public sealed record SeriesDto(string Name, IReadOnlyList<double?> Values);

public sealed record CorrelationDto(IReadOnlyList<string> Names, IReadOnlyList<IReadOnlyList<double?>> Matrix);

public sealed record RiskDto(
    ScopeDto Scope,
    DateOnly AsOf,
    TotalsDto Totals,
    VarDto Var,
    decimal TopFiveShareOfGross,
    ScenarioGridDto Scenario,
    IReadOnlyList<ScenarioRowDto> ScenarioByChild,
    IReadOnlyList<AssetBetaDto> Assets,
    IReadOnlyList<MandateCheckDto> Mandate,
    IReadOnlyList<VolatilityRowDto> Volatility,
    IReadOnlyList<DateOnly> RollingDates,
    IReadOnlyList<SeriesDto> RollingVolatility,
    CorrelationDto Correlation
);

public sealed record LiquidityLineDto(string Asset, decimal GrossUsd, decimal? AverageDailyVolumeUsd, decimal? DailyCapacityUsd, double? Days, int Managers);

public sealed record LadderDto(decimal WithinOneDayUsd, decimal OneToFiveDaysUsd, decimal FiveToTwentyDaysUsd, decimal BeyondTwentyDaysUsd, decimal UnknownUsd, decimal TotalUsd);

public sealed record LadderRowDto(PerformanceScope Kind, int Id, string Name, LadderDto Ladder);

public sealed record QueueLineDto(string Manager, string Instrument, decimal ExposureUsd, double? DaysAlone);

/// <param name="SlowestQueue">Who holds the slowest asset, and how long each would need alone.</param>
public sealed record LiquidityDto(
    ScopeDto Scope,
    DateOnly AsOf,
    decimal Participation,
    decimal NavUsd,
    LadderDto Ladder,
    IReadOnlyList<LadderRowDto> Children,
    IReadOnlyList<LiquidityLineDto> Lines,
    IReadOnlyList<QueueLineDto> SlowestQueue
);

/// <summary>What alert rules and mandate limits look at for one fund, sleeve or manager. Ratios are fractions of its NAV.</summary>
/// <param name="WorstDrawdown">Current drawdown of the manager furthest below its peak, negative.</param>
/// <param name="SlowestDays">Days to close the slowest asset at the default share of daily volume.</param>
public sealed record ScopeMetrics(
    PerformanceScope Kind,
    int Id,
    string Name,
    int? SleeveId,
    decimal NavUsd,
    decimal GrossOverNav,
    decimal NetOverNav,
    string? LargestAsset,
    decimal LargestAssetOverNav,
    string? WorstManager,
    double? WorstDrawdown,
    string? SlowestAsset,
    double? SlowestDays,
    decimal? VarOverNav,
    double? DayReturn
);

public sealed record FundMetrics(DateOnly AsOf, IReadOnlyList<ScopeMetrics> Scopes);

using KatalepsisFT.Domain.Benchmarks;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Application.Performance;

public enum PerformanceScope
{
    Fund,
    Sleeve,
    Manager,
}

public enum PerformancePeriod
{
    Ytd,
    Days30,
    Days90,
    SinceInception,
}

public sealed record BenchmarkDto(int Id, string Name, BenchmarkKind Kind, bool IsApproximation);

public sealed record ScopeDto(PerformanceScope Kind, int Id, string Name, int FundId, string FundName, bool IsDemo, int? SleeveId, string? SleeveName);

/// <summary>Fractions, not percentages. Null when undefined, for example Sharpe of a series that does not move.</summary>
public sealed record StatsDto(double TotalReturn, double? Volatility, double? Sharpe, double? Sortino, double? MaxDrawdown, double? Calmar);

public sealed record ComparisonDto(double ExcessReturn, double? Beta, double? Correlation, double? TrackingError, double? InformationRatio);

/// <param name="Portfolio">Null when the history does not reach back to the start of the window.</param>
public sealed record PeriodReturnDto(string Label, double? Portfolio, double? Benchmark);

public sealed record MonthlyReturnDto(string Month, double Portfolio, double Benchmark);

/// <param name="Contribution">The part of the scope's return over the period that this child produced. The children's contributions add up to the scope's return.</param>
/// <param name="PnlUsd">What the child made or lost over the period, net of money in and out.</param>
/// <param name="AverageWeight">The child's average share of the scope's NAV over the period.</param>
public sealed record ChildPerformanceDto(PerformanceScope Kind, int Id, string Name, decimal NavUsd, StatsDto Stats, ComparisonDto Comparison, double Contribution, decimal PnlUsd, double AverageWeight);

public sealed record PnlLineDto(string Key, decimal PnlUsd);

/// <param name="NavChangeUsd">What NAV actually did over the period, net of money in and out.</param>
/// <param name="ExplainedUsd">What the positions held at each close earned on the next day's price move.</param>
public sealed record AttributionDto(
    decimal StartNavUsd,
    decimal NavChangeUsd,
    decimal ExplainedUsd,
    IReadOnlyList<PnlLineDto> ByAsset,
    IReadOnlyList<PnlLineDto> ByType,
    IReadOnlyList<PnlLineDto> ByChild
);

public sealed record PerformanceDto(
    ScopeDto Scope,
    BenchmarkDto Benchmark,
    IReadOnlyList<BenchmarkDto> Benchmarks,
    PerformancePeriod Period,
    DateOnly From,
    DateOnly AsOf,
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<double> Portfolio,
    IReadOnlyList<double> BenchmarkSeries,
    IReadOnlyList<double> Drawdown,
    StatsDto Stats,
    StatsDto BenchmarkStats,
    ComparisonDto Comparison,
    IReadOnlyList<PeriodReturnDto> Periods,
    IReadOnlyList<MonthlyReturnDto> Monthly,
    IReadOnlyList<ChildPerformanceDto> Children,
    AttributionDto Attribution
);

public sealed record AssetPerformanceDto(
    string Symbol,
    BenchmarkDto Benchmark,
    IReadOnlyList<BenchmarkDto> Benchmarks,
    PerformancePeriod Period,
    DateOnly From,
    DateOnly AsOf,
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<double> Asset,
    IReadOnlyList<double> BenchmarkSeries,
    StatsDto Stats,
    StatsDto BenchmarkStats,
    ComparisonDto Comparison
);

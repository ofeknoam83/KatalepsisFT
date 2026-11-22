// Copied from Katalepsis (Horizen.Ria/Backtests/BacktestReport.cs, commit 9847e4f): the two types the calculator needs.
// Only the namespace was changed.
using System.Text.Json.Serialization;

namespace KatalepsisFT.Analytics.Performance;

/// <summary>
/// Performance metrics on daily returns (365-day year, risk-free rate 0), as in Tools/StrategyResearch/research/metrics.py.
/// Fractions, not percentages. <see cref="MaxDrawdown"/> is negative (−0.25 = 25% below the peak).
/// Null when undefined (e.g. Sharpe of a flat series).
/// </summary>
public sealed class BacktestMetrics
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? Cagr { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? Volatility { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? Sharpe { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? Sortino { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? MaxDrawdown { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? Calmar { get; set; }

    /// <summary>Annualised one-way turnover, excluding the initial allocation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? Turnover { get; set; }

    /// <summary>Annualised trading costs as a fraction of NAV (initial allocation included).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? CostDrag { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? TotalReturn { get; set; }
}

public static class BacktestConstants
{
    public const string EngineVersion = "ria-backtest 1.0";
    public const int MaxChartPoints = 400;
    public const int DaysPerYear = 365;
}

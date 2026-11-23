using KatalepsisFT.Domain.Funds;

namespace KatalepsisFT.Domain.Alerts;

public enum AlertScopeKind
{
    Fund = 1,
    Sleeve = 2,
    Manager = 3,
}

/// <summary>What a rule watches. Ratios are fractions of the scope's NAV unless stated.</summary>
public enum AlertMetric
{
    GrossExposureOverNav = 1,
    NetExposureOverNav = 2,

    /// <summary>Gross exposure to the largest single asset over NAV.</summary>
    LargestAssetOverNav = 3,

    /// <summary>Current drawdown of the manager furthest below its peak, negative.</summary>
    DeepestManagerDrawdown = 4,

    /// <summary>Days to close the slowest asset at 10% of its average daily volume.</summary>
    SlowestDaysToLiquidate = 5,

    /// <summary>1-day 95% value at risk over NAV.</summary>
    ValueAtRiskOverNav = 6,

    /// <summary>Return of the latest day.</summary>
    DayReturn = 7,

    /// <summary>Share of NAV held on the single largest exchange or wallet type: how much one counterparty failing would touch.</summary>
    LargestVenueOverNav = 8,
}

public enum AlertOperator
{
    Above = 1,
    Below = 2,
}

public enum AlertSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3,
}

/// <summary>A condition on a fund, a sleeve or a manager that raises an alert while it holds.</summary>
public class AlertRule
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public AlertScopeKind ScopeKind { get; set; }

    /// <summary>The fund, sleeve or manager id, according to <see cref="ScopeKind"/>.</summary>
    public int ScopeId { get; set; }
    public AlertMetric Metric { get; set; }
    public AlertOperator Operator { get; set; }
    public decimal Threshold { get; set; }
    public AlertSeverity Severity { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Set on rules kept in step with a sleeve's mandate limit; their condition is changed by changing the limit.</summary>
    public int? MandateLimitId { get; set; }
    public MandateLimit? MandateLimit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Alert> Alerts { get; set; } = [];

    public bool IsBreachedBy(decimal value) => Operator == AlertOperator.Above ? value > Threshold : value < Threshold;
}

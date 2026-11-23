using System.Globalization;
using KatalepsisFT.Domain.Alerts;

namespace KatalepsisFT.Application.Alerts;

/// <summary>The signed-in person an alert action is recorded against.</summary>
public sealed record Actor(int UserId, string Name);

public enum AlertListState
{
    Active,
    Resolved,
    All,
}

public sealed record AlertActionDto(AlertActionKind Kind, DateTimeOffset At, string? UserName, string? Note);

/// <param name="Description">The condition in words, with the current figure and the limit.</param>
/// <param name="IsSnoozed">Still active, but hidden from the count of alerts needing attention until <paramref name="SnoozedUntil"/>.</param>
public sealed record AlertDto(
    int Id,
    int RuleId,
    AlertStatus Status,
    AlertSeverity Severity,
    AlertScopeKind ScopeKind,
    int ScopeId,
    string ScopeName,
    int? SleeveId,
    AlertMetric Metric,
    string Description,
    string? Subject,
    decimal Value,
    decimal Threshold,
    bool FromMandate,
    DateOnly DataDate,
    DateTimeOffset RaisedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt,
    int? AssigneeUserId,
    string? AssigneeName,
    DateTimeOffset? SnoozedUntil,
    bool IsSnoozed,
    IReadOnlyList<AlertActionDto> Actions
);

public sealed record AlertCountsDto(int Active, int Critical, int NeedingAttention, int Unassigned, int Snoozed);

/// <summary>One breach of a rule, from the moment it was seen until it ended, for the record.</summary>
/// <param name="Rule">The rule in words.</param>
/// <param name="PeakValue">The figure at its furthest past the limit.</param>
/// <param name="Outcome">How it ended, or that it has not.</param>
/// <param name="Note">What was written when it was resolved, or the last comment.</param>
public sealed record BreachDto(
    int Id,
    AlertSeverity Severity,
    AlertScopeKind ScopeKind,
    string ScopeName,
    string Rule,
    string? Subject,
    bool FromMandate,
    AlertMetric Metric,
    DateTimeOffset RaisedAt,
    DateTimeOffset? EndedAt,
    double DurationHours,
    decimal Threshold,
    decimal PeakValue,
    decimal LastValue,
    AlertStatus Status,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAt,
    string Outcome,
    string? Note
);

public sealed record BreachRegisterDto(DateOnly From, DateOnly To, int Breaches, int MandateBreaches, int StillOpen, IReadOnlyList<BreachDto> Rows);

public sealed record AlertListDto(AlertCountsDto Counts, IReadOnlyList<AlertDto> Alerts);

public sealed record AlertRuleDto(
    int Id,
    AlertScopeKind ScopeKind,
    int ScopeId,
    string ScopeName,
    AlertMetric Metric,
    AlertOperator Operator,
    decimal Threshold,
    AlertSeverity Severity,
    bool IsEnabled,
    bool FromMandate,
    string Description,
    bool HasActiveAlert
);

public sealed record AlertScopeOptionDto(AlertScopeKind Kind, int Id, string Name);

public sealed record AlertRulesDto(IReadOnlyList<AlertRuleDto> Rules, IReadOnlyList<AlertScopeOptionDto> Scopes);

public sealed record SaveAlertRuleRequest(AlertScopeKind ScopeKind, int ScopeId, AlertMetric Metric, AlertOperator Operator, decimal Threshold, AlertSeverity Severity, bool IsEnabled);

public sealed record AssignRequest(int? UserId);

public sealed record NoteRequest(string? Note);

public sealed record SnoozeRequest(int Days, string? Note);

public sealed record EvaluationResultDto(int Raised, int Recovered, int Active);

/// <summary>Outcome of a change: the new state, or why it was refused. Both null means the target does not exist.</summary>
public sealed record AlertChange<T>(T? Value, string? Error)
    where T : class
{
    public static AlertChange<T> NotFound { get; } = new(null, null);

    public static AlertChange<T> Refused(string error) => new(null, error);
}

/// <summary>How metrics, figures and conditions are written for people.</summary>
public static class AlertText
{
    public static string Label(AlertMetric metric, string? subject) =>
        metric switch
        {
            AlertMetric.GrossExposureOverNav => "Gross exposure ÷ NAV",
            AlertMetric.NetExposureOverNav => "Net exposure ÷ NAV",
            AlertMetric.LargestAssetOverNav => Named("Largest single asset", subject),
            AlertMetric.DeepestManagerDrawdown => Named("Deepest manager drawdown", subject),
            AlertMetric.SlowestDaysToLiquidate => Named("Slowest asset, days to liquidate", subject),
            AlertMetric.ValueAtRiskOverNav => "1-day value at risk ÷ NAV",
            AlertMetric.DayReturn => "Day return",
            AlertMetric.LargestVenueOverNav => Named("Largest share on one venue", subject),
            _ => metric.ToString(),
        };

    public static string Figure(AlertMetric metric, decimal value) =>
        metric == AlertMetric.SlowestDaysToLiquidate
            ? value.ToString("0.0", CultureInfo.InvariantCulture) + " days"
            : (value * 100m).ToString("0.0", CultureInfo.InvariantCulture).Replace("-", "−", StringComparison.Ordinal) + "%";

    public static string Condition(AlertMetric metric, AlertOperator op, decimal threshold) => $"{(op == AlertOperator.Above ? "above" : "below")} {Figure(metric, threshold)}";

    private static string Named(string label, string? subject) => subject is null ? label : $"{label} ({subject})";
}

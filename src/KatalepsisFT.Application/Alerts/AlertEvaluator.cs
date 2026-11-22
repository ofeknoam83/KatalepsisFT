using KatalepsisFT.Application.Performance;
using KatalepsisFT.Application.Risk;
using KatalepsisFT.Domain.Alerts;
using KatalepsisFT.Domain.Funds;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Alerts;

/// <summary>
/// Compares every rule of a fund with the latest figures. A condition that starts to hold raises one alert; while it
/// keeps holding that alert is only refreshed; when it stops the alert is closed as recovered.
/// </summary>
public sealed class AlertEvaluator(IFtDbContext database, RiskService risk, TimeProvider clock)
{
    public const string RuleSwitchedOff = "The rule was switched off.";

    public async Task<EvaluationResultDto> EvaluateAsync(int fundId, CancellationToken cancellationToken)
    {
        await SyncMandateRulesAsync(fundId, cancellationToken);
        List<AlertRule> rules = await database.AlertRules.Include(r => r.Alerts).Where(r => r.FundId == fundId).ToListAsync(cancellationToken);
        FundMetrics? metrics = await risk.GetFundMetricsAsync(fundId, cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        int raised = 0,
            recovered = 0;

        foreach (AlertRule rule in rules)
        {
            Alert? latest = rule.Alerts.OrderByDescending(a => a.RaisedAt).ThenByDescending(a => a.Id).FirstOrDefault();
            if (!rule.IsEnabled)
            {
                if (latest is { IsActive: true })
                {
                    Close(latest, now, RuleSwitchedOff);
                    recovered++;
                }

                continue;
            }

            ScopeMetrics? scope = metrics?.Scopes.FirstOrDefault(s => s.Kind.ToString() == rule.ScopeKind.ToString() && s.Id == rule.ScopeId);
            (decimal Value, string? Subject)? figure = scope is null ? null : Figure(rule.Metric, scope);
            if (figure is null)
            {
                // No figure, no verdict: an alert is neither raised nor closed on missing data.
                continue;
            }

            if (rule.IsBreachedBy(figure.Value.Value))
            {
                if (latest is { IsActive: true })
                {
                    latest.Value = figure.Value.Value;
                    latest.Subject = figure.Value.Subject;
                    latest.Threshold = rule.Threshold;
                    latest.LastSeenAt = now;
                    latest.DataDate = metrics!.AsOf;
                }
                else if (latest is not { ResolvedWhileBreaching: true })
                {
                    Alert alert = new()
                    {
                        Status = AlertStatus.Open,
                        Subject = figure.Value.Subject,
                        Value = figure.Value.Value,
                        Threshold = rule.Threshold,
                        DataDate = metrics!.AsOf,
                        RaisedAt = now,
                        LastSeenAt = now,
                    };
                    alert.Record(AlertActionKind.Raised, now, null, null);
                    rule.Alerts.Add(alert);
                    raised++;
                }
            }
            else if (latest is { IsActive: true })
            {
                Close(latest, now, null);
                recovered++;
            }
            else if (latest is { ResolvedWhileBreaching: true })
            {
                latest.ResolvedWhileBreaching = false;
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        return new EvaluationResultDto(raised, recovered, rules.Count(r => r.Alerts.Any(a => a.IsActive)));
    }

    /// <summary>The figure a metric reads for a scope, and the asset or manager it is about; null when there is none.</summary>
    public static (decimal Value, string? Subject)? Figure(AlertMetric metric, ScopeMetrics scope) =>
        metric switch
        {
            AlertMetric.GrossExposureOverNav => (scope.GrossOverNav, null),
            AlertMetric.NetExposureOverNav => (scope.NetOverNav, null),
            AlertMetric.LargestAssetOverNav => scope.LargestAsset is null ? null : (scope.LargestAssetOverNav, scope.LargestAsset),
            AlertMetric.DeepestManagerDrawdown => scope.WorstDrawdown is double drawdown ? (Math.Round((decimal)drawdown, 6), scope.Kind == PerformanceScope.Manager ? null : scope.WorstManager) : null,
            AlertMetric.SlowestDaysToLiquidate => scope.SlowestDays is double days ? (Math.Round((decimal)days, 2), scope.SlowestAsset) : null,
            AlertMetric.ValueAtRiskOverNav => scope.VarOverNav is decimal var ? (var, null) : null,
            AlertMetric.DayReturn => scope.DayReturn is double day ? (Math.Round((decimal)day, 6), null) : null,
            _ => null,
        };

    public static (AlertMetric Metric, AlertOperator Operator) ForMandate(MandateLimitKind kind) =>
        kind switch
        {
            MandateLimitKind.GrossExposureMax => (AlertMetric.GrossExposureOverNav, AlertOperator.Above),
            MandateLimitKind.NetExposureMin => (AlertMetric.NetExposureOverNav, AlertOperator.Below),
            MandateLimitKind.NetExposureMax => (AlertMetric.NetExposureOverNav, AlertOperator.Above),
            MandateLimitKind.SingleAssetMax => (AlertMetric.LargestAssetOverNav, AlertOperator.Above),
            MandateLimitKind.ManagerDrawdownMin => (AlertMetric.DeepestManagerDrawdown, AlertOperator.Below),
            MandateLimitKind.DaysToLiquidateMax => (AlertMetric.SlowestDaysToLiquidate, AlertOperator.Above),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown mandate limit."),
        };

    private static void Close(Alert alert, DateTimeOffset now, string? note)
    {
        alert.Status = AlertStatus.Resolved;
        alert.ResolvedAt = now;
        alert.SnoozedUntil = null;
        alert.Record(AlertActionKind.Recovered, now, null, null, note);
    }

    /// <summary>Every mandate limit has exactly one rule, critical by default, whose threshold follows the limit.</summary>
    private async Task SyncMandateRulesAsync(int fundId, CancellationToken cancellationToken)
    {
        List<MandateLimit> limits = await database.MandateLimits.Where(l => l.Sleeve.FundId == fundId).ToListAsync(cancellationToken);
        List<AlertRule> existing = await database.AlertRules.Where(r => r.FundId == fundId && r.MandateLimitId != null).ToListAsync(cancellationToken);
        foreach (MandateLimit limit in limits)
        {
            (AlertMetric metric, AlertOperator op) = ForMandate(limit.Kind);
            AlertRule? rule = existing.FirstOrDefault(r => r.MandateLimitId == limit.Id);
            if (rule is null)
            {
                database.AlertRules.Add(
                    new AlertRule
                    {
                        FundId = fundId,
                        ScopeKind = AlertScopeKind.Sleeve,
                        ScopeId = limit.SleeveId,
                        Metric = metric,
                        Operator = op,
                        Threshold = limit.Value,
                        Severity = AlertSeverity.Critical,
                        MandateLimitId = limit.Id,
                        CreatedAt = clock.GetUtcNow(),
                    }
                );
            }
            else
            {
                rule.Threshold = limit.Value;
            }
        }

        await database.SaveChangesAsync(cancellationToken);
    }
}

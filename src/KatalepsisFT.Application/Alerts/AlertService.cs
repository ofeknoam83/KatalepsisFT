using KatalepsisFT.Domain.Alerts;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Alerts;

/// <summary>The alert inbox, the workflow on an alert, and the rules behind them.</summary>
public sealed class AlertService(IFtDbContext database, AlertEvaluator evaluator, TimeProvider clock)
{
    public const int MaxSnoozeDays = 30;

    private sealed record ScopeName(string Name, int? SleeveId);

    // ---------------------------------------------------------------- inbox

    public async Task<AlertListDto?> ListAsync(int fundId, AlertListState state, int? sleeveId, CancellationToken cancellationToken)
    {
        Dictionary<(AlertScopeKind, int), ScopeName>? scopes = await ScopesAsync(fundId, cancellationToken);
        if (scopes is null)
        {
            return null;
        }

        List<Alert> alerts = await database
            .Alerts.AsNoTracking()
            .Include(a => a.Rule)
            .Include(a => a.Actions)
            .Where(a => a.Rule.FundId == fundId)
            .ToListAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        List<AlertDto> all = alerts
            .Select(a => Dto(a, scopes, now))
            .Where(a => sleeveId is null || a.SleeveId == sleeveId)
            .OrderBy(a => a.Status == AlertStatus.Resolved)
            .ThenBy(a => a.IsSnoozed)
            .ThenByDescending(a => a.Severity)
            .ThenByDescending(a => a.RaisedAt)
            .ThenByDescending(a => a.Id)
            .ToList();
        List<AlertDto> active = all.Where(a => a.Status != AlertStatus.Resolved).ToList();
        AlertCountsDto counts = new(
            active.Count,
            active.Count(a => a.Severity == AlertSeverity.Critical),
            active.Count(a => a.Status == AlertStatus.Open && !a.IsSnoozed),
            active.Count(a => a.AssigneeUserId is null),
            active.Count(a => a.IsSnoozed)
        );
        IReadOnlyList<AlertDto> shown = state switch
        {
            AlertListState.Active => active,
            AlertListState.Resolved => all.Where(a => a.Status == AlertStatus.Resolved).ToList(),
            _ => all,
        };
        return new AlertListDto(counts, shown);
    }

    // ---------------------------------------------------------------- workflow

    public Task<AlertChange<AlertDto>> AcknowledgeAsync(int alertId, Actor actor, CancellationToken cancellationToken) =>
        ChangeAsync(
            alertId,
            alert =>
            {
                if (alert.Status != AlertStatus.Open)
                {
                    return "Only an open alert can be acknowledged.";
                }

                alert.Status = AlertStatus.Acknowledged;
                alert.Record(AlertActionKind.Acknowledged, clock.GetUtcNow(), actor.UserId, actor.Name);
                return null;
            },
            cancellationToken
        );

    /// <param name="assignee">Null takes the alert away from whoever has it.</param>
    public Task<AlertChange<AlertDto>> AssignAsync(int alertId, Actor actor, Actor? assignee, CancellationToken cancellationToken) =>
        ChangeAsync(
            alertId,
            alert =>
            {
                if (!alert.IsActive)
                {
                    return "A resolved alert cannot be assigned.";
                }

                alert.AssigneeUserId = assignee?.UserId;
                alert.AssigneeName = assignee?.Name;
                alert.Record(AlertActionKind.Assigned, clock.GetUtcNow(), actor.UserId, actor.Name, assignee is null ? "Unassigned" : $"Assigned to {assignee.Name}");
                return null;
            },
            cancellationToken
        );

    public Task<AlertChange<AlertDto>> CommentAsync(int alertId, Actor actor, string note, CancellationToken cancellationToken) =>
        ChangeAsync(
            alertId,
            alert =>
            {
                alert.Record(AlertActionKind.Commented, clock.GetUtcNow(), actor.UserId, actor.Name, note.Trim());
                return null;
            },
            cancellationToken
        );

    public Task<AlertChange<AlertDto>> SnoozeAsync(int alertId, Actor actor, int days, string? note, CancellationToken cancellationToken) =>
        ChangeAsync(
            alertId,
            alert =>
            {
                if (!alert.IsActive)
                {
                    return "A resolved alert cannot be snoozed.";
                }

                DateTimeOffset now = clock.GetUtcNow();
                alert.SnoozedUntil = now.AddDays(days);
                alert.Record(AlertActionKind.Snoozed, now, actor.UserId, actor.Name, string.IsNullOrWhiteSpace(note) ? $"For {days} day(s)" : $"For {days} day(s): {note.Trim()}");
                return null;
            },
            cancellationToken
        );

    public Task<AlertChange<AlertDto>> ResolveAsync(int alertId, Actor actor, string? note, CancellationToken cancellationToken) =>
        ChangeAsync(
            alertId,
            alert =>
            {
                if (!alert.IsActive)
                {
                    return "The alert is already resolved.";
                }

                DateTimeOffset now = clock.GetUtcNow();
                alert.Status = AlertStatus.Resolved;
                alert.ResolvedAt = now;
                alert.SnoozedUntil = null;
                // The condition still holds, or the evaluator would have closed the alert itself.
                alert.ResolvedWhileBreaching = true;
                alert.Record(AlertActionKind.Resolved, now, actor.UserId, actor.Name, string.IsNullOrWhiteSpace(note) ? null : note.Trim());
                return null;
            },
            cancellationToken
        );

    private async Task<AlertChange<AlertDto>> ChangeAsync(int alertId, Func<Alert, string?> change, CancellationToken cancellationToken)
    {
        Alert? alert = await database.Alerts.Include(a => a.Rule).Include(a => a.Actions).FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return AlertChange<AlertDto>.NotFound;
        }

        string? error = change(alert);
        if (error is not null)
        {
            return AlertChange<AlertDto>.Refused(error);
        }

        await database.SaveChangesAsync(cancellationToken);
        Dictionary<(AlertScopeKind, int), ScopeName> scopes = (await ScopesAsync(alert.Rule.FundId, cancellationToken))!;
        return new AlertChange<AlertDto>(Dto(alert, scopes, clock.GetUtcNow()), null);
    }

    // ---------------------------------------------------------------- rules

    public async Task<AlertRulesDto?> ListRulesAsync(int fundId, CancellationToken cancellationToken)
    {
        Dictionary<(AlertScopeKind, int), ScopeName>? scopes = await ScopesAsync(fundId, cancellationToken);
        if (scopes is null)
        {
            return null;
        }

        var rules = await database
            .AlertRules.AsNoTracking()
            .Where(r => r.FundId == fundId)
            .Select(r => new { Rule = r, HasActive = r.Alerts.Any(a => a.Status != AlertStatus.Resolved) })
            .ToListAsync(cancellationToken);
        return new AlertRulesDto(
            rules
                .Select(r => RuleDto(r.Rule, scopes, r.HasActive))
                .OrderBy(r => r.ScopeKind)
                .ThenBy(r => r.ScopeName, StringComparer.Ordinal)
                .ThenBy(r => r.Metric)
                .ThenBy(r => r.Id)
                .ToList(),
            scopes.Select(s => new AlertScopeOptionDto(s.Key.Item1, s.Key.Item2, s.Value.Name)).OrderBy(s => s.Kind).ThenBy(s => s.Id).ToList()
        );
    }

    public async Task<AlertChange<AlertRuleDto>> CreateRuleAsync(int fundId, SaveAlertRuleRequest request, CancellationToken cancellationToken)
    {
        Dictionary<(AlertScopeKind, int), ScopeName>? scopes = await ScopesAsync(fundId, cancellationToken);
        if (scopes is null)
        {
            return AlertChange<AlertRuleDto>.NotFound;
        }

        if (!scopes.ContainsKey((request.ScopeKind, request.ScopeId)))
        {
            return AlertChange<AlertRuleDto>.Refused("That fund, sleeve or manager is not part of this fund.");
        }

        AlertRule rule = new()
        {
            FundId = fundId,
            ScopeKind = request.ScopeKind,
            ScopeId = request.ScopeId,
            Metric = request.Metric,
            Operator = request.Operator,
            Threshold = request.Threshold,
            Severity = request.Severity,
            IsEnabled = request.IsEnabled,
            CreatedAt = clock.GetUtcNow(),
        };
        database.AlertRules.Add(rule);
        await database.SaveChangesAsync(cancellationToken);
        await evaluator.EvaluateAsync(fundId, cancellationToken);
        return new AlertChange<AlertRuleDto>(await RuleDtoAsync(rule, scopes, cancellationToken), null);
    }

    /// <summary>A rule that follows a mandate limit keeps its scope and condition; only its severity and on/off switch change.</summary>
    public async Task<AlertChange<AlertRuleDto>> UpdateRuleAsync(int ruleId, SaveAlertRuleRequest request, CancellationToken cancellationToken)
    {
        AlertRule? rule = await database.AlertRules.FirstOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return AlertChange<AlertRuleDto>.NotFound;
        }

        Dictionary<(AlertScopeKind, int), ScopeName> scopes = (await ScopesAsync(rule.FundId, cancellationToken))!;
        if (rule.MandateLimitId is null)
        {
            if (!scopes.ContainsKey((request.ScopeKind, request.ScopeId)))
            {
                return AlertChange<AlertRuleDto>.Refused("That fund, sleeve or manager is not part of this fund.");
            }

            rule.ScopeKind = request.ScopeKind;
            rule.ScopeId = request.ScopeId;
            rule.Metric = request.Metric;
            rule.Operator = request.Operator;
            rule.Threshold = request.Threshold;
        }

        rule.Severity = request.Severity;
        rule.IsEnabled = request.IsEnabled;
        await database.SaveChangesAsync(cancellationToken);
        await evaluator.EvaluateAsync(rule.FundId, cancellationToken);
        return new AlertChange<AlertRuleDto>(await RuleDtoAsync(rule, scopes, cancellationToken), null);
    }

    /// <summary>Deletes a rule with its alerts and their history. Mandate rules go only when the limit goes.</summary>
    public async Task<AlertChange<AlertRuleDto>> DeleteRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        AlertRule? rule = await database.AlertRules.FirstOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return AlertChange<AlertRuleDto>.NotFound;
        }

        if (rule.MandateLimitId is not null)
        {
            return AlertChange<AlertRuleDto>.Refused("This rule follows a mandate limit. Switch it off instead of deleting it.");
        }

        Dictionary<(AlertScopeKind, int), ScopeName> scopes = (await ScopesAsync(rule.FundId, cancellationToken))!;
        AlertRuleDto dto = RuleDto(rule, scopes, false);
        database.AlertRules.Remove(rule);
        await database.SaveChangesAsync(cancellationToken);
        return new AlertChange<AlertRuleDto>(dto, null);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>Names of everything a rule can point at in a fund; null when the fund does not exist.</summary>
    private async Task<Dictionary<(AlertScopeKind, int), ScopeName>?> ScopesAsync(int fundId, CancellationToken cancellationToken)
    {
        string? fundName = await database.Funds.Where(f => f.Id == fundId).Select(f => f.Name).FirstOrDefaultAsync(cancellationToken);
        if (fundName is null)
        {
            return null;
        }

        Dictionary<(AlertScopeKind, int), ScopeName> scopes = new() { [(AlertScopeKind.Fund, fundId)] = new ScopeName(fundName, null) };
        foreach (var sleeve in await database.Sleeves.Where(s => s.FundId == fundId).OrderBy(s => s.SortOrder).Select(s => new { s.Id, s.Name }).ToListAsync(cancellationToken))
        {
            scopes[(AlertScopeKind.Sleeve, sleeve.Id)] = new ScopeName(sleeve.Name, sleeve.Id);
        }

        foreach (var manager in await database.ManagerAccounts.Where(m => m.Sleeve.FundId == fundId).Select(m => new { m.Id, m.Name, m.SleeveId }).ToListAsync(cancellationToken))
        {
            scopes[(AlertScopeKind.Manager, manager.Id)] = new ScopeName(manager.Name, manager.SleeveId);
        }

        return scopes;
    }

    private async Task<AlertRuleDto> RuleDtoAsync(AlertRule rule, Dictionary<(AlertScopeKind, int), ScopeName> scopes, CancellationToken cancellationToken) =>
        RuleDto(rule, scopes, await database.Alerts.AnyAsync(a => a.RuleId == rule.Id && a.Status != AlertStatus.Resolved, cancellationToken));

    private static AlertRuleDto RuleDto(AlertRule rule, Dictionary<(AlertScopeKind, int), ScopeName> scopes, bool hasActive) =>
        new(
            rule.Id,
            rule.ScopeKind,
            rule.ScopeId,
            scopes.TryGetValue((rule.ScopeKind, rule.ScopeId), out ScopeName? scope) ? scope.Name : "Removed",
            rule.Metric,
            rule.Operator,
            rule.Threshold,
            rule.Severity,
            rule.IsEnabled,
            rule.MandateLimitId is not null,
            $"{AlertText.Label(rule.Metric, null)} {AlertText.Condition(rule.Metric, rule.Operator, rule.Threshold)}",
            hasActive
        );

    private static AlertDto Dto(Alert alert, Dictionary<(AlertScopeKind, int), ScopeName> scopes, DateTimeOffset now)
    {
        AlertRule rule = alert.Rule;
        scopes.TryGetValue((rule.ScopeKind, rule.ScopeId), out ScopeName? scope);
        return new AlertDto(
            alert.Id,
            rule.Id,
            alert.Status,
            rule.Severity,
            rule.ScopeKind,
            rule.ScopeId,
            scope?.Name ?? "Removed",
            scope?.SleeveId,
            rule.Metric,
            $"{AlertText.Label(rule.Metric, alert.Subject)} is {AlertText.Figure(rule.Metric, alert.Value)}, {AlertText.Condition(rule.Metric, rule.Operator, alert.Threshold)}",
            alert.Subject,
            alert.Value,
            alert.Threshold,
            rule.MandateLimitId is not null,
            alert.DataDate,
            alert.RaisedAt,
            alert.LastSeenAt,
            alert.ResolvedAt,
            alert.AssigneeUserId,
            alert.AssigneeName,
            alert.SnoozedUntil,
            alert.IsActive && alert.SnoozedUntil is DateTimeOffset until && until > now,
            alert.Actions.OrderBy(a => a.At).ThenBy(a => a.Id).Select(a => new AlertActionDto(a.Kind, a.At, a.UserName, a.Note)).ToList()
        );
    }
}

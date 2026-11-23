using KatalepsisFT.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Audit;

/// <summary>What a change is about, looked up before the change is made so that a removal can still be named.</summary>
public sealed record AuditTarget(int? FundId, string? FundName, string? Subject)
{
    public static AuditTarget None { get; } = new(null, null, null);
}

public sealed record AuditEntryDto(long Id, DateTimeOffset At, string UserName, string Action, int? FundId, string? FundName, string? Subject, string? Detail);

public sealed record AuditListDto(DateOnly From, DateOnly To, bool Truncated, IReadOnlyList<AuditEntryDto> Entries);

/// <summary>The record of who changed what. Written by the web layer after a change succeeds; never edited.</summary>
public sealed class AuditLog(IFtDbContext database, TimeProvider clock)
{
    public const int MaxEntries = 2000;

    public async Task RecordAsync(string userName, string action, AuditTarget target, string? detail, CancellationToken cancellationToken)
    {
        database.AuditEntries.Add(
            new AuditEntry
            {
                At = clock.GetUtcNow(),
                UserName = Cut(userName, 256)!,
                Action = Cut(action, 100)!,
                FundId = target.FundId,
                FundName = Cut(target.FundName, 200),
                Subject = Cut(target.Subject, 400),
                Detail = Cut(detail, 2000),
            }
        );
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <param name="fundId">Only this fund's entries; null for everything, including sign-ins.</param>
    public async Task<AuditListDto> ListAsync(int? fundId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        DateTimeOffset start = new(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        DateTimeOffset end = new(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        List<AuditEntryDto> entries = await database
            .AuditEntries.AsNoTracking()
            .Where(e => e.At >= start && e.At < end && (fundId == null || e.FundId == fundId))
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .Take(MaxEntries + 1)
            .Select(e => new AuditEntryDto(e.Id, e.At, e.UserName, e.Action, e.FundId, e.FundName, e.Subject, e.Detail))
            .ToListAsync(cancellationToken);
        return new AuditListDto(from, to, entries.Count > MaxEntries, entries.Take(MaxEntries).ToList());
    }

    public async Task<AuditTarget> FundAsync(int fundId, CancellationToken cancellationToken)
    {
        string? name = await database.Funds.Where(f => f.Id == fundId).Select(f => f.Name).FirstOrDefaultAsync(cancellationToken);
        return name is null ? AuditTarget.None : new AuditTarget(fundId, name, null);
    }

    public async Task<AuditTarget> SleeveAsync(int sleeveId, CancellationToken cancellationToken)
    {
        var sleeve = await database.Sleeves.Where(s => s.Id == sleeveId).Select(s => new { s.Name, s.FundId, Fund = s.Fund.Name }).FirstOrDefaultAsync(cancellationToken);
        return sleeve is null ? AuditTarget.None : new AuditTarget(sleeve.FundId, sleeve.Fund, sleeve.Name);
    }

    public async Task<AuditTarget> ManagerAsync(int managerId, CancellationToken cancellationToken, string? what = null)
    {
        var manager = await database.ManagerAccounts.Where(m => m.Id == managerId).Select(m => new { m.Name, Sleeve = m.Sleeve.Name, m.Sleeve.FundId, Fund = m.Sleeve.Fund.Name }).FirstOrDefaultAsync(cancellationToken);
        return manager is null ? AuditTarget.None : new AuditTarget(manager.FundId, manager.Fund, $"{manager.Sleeve} / {manager.Name}{(what is null ? string.Empty : " / " + what)}");
    }

    public async Task<AuditTarget> ConnectionAsync(int connectionId, CancellationToken cancellationToken)
    {
        var connection = await database.VenueConnections.Where(c => c.Id == connectionId).Select(c => new { c.ManagerAccountId, c.Venue, c.Label, c.KeyHint }).FirstOrDefaultAsync(cancellationToken);
        return connection is null ? AuditTarget.None : await ManagerAsync(connection.ManagerAccountId, cancellationToken, $"{Risk.VenueExposureService.NameOf(connection.Venue)} \"{connection.Label}\" …{connection.KeyHint}");
    }

    public async Task<AuditTarget> PositionAsync(int positionId, CancellationToken cancellationToken)
    {
        var position = await database.ManualPositions.Where(p => p.Id == positionId).Select(p => new { p.ManagerAccountId, p.Type, p.Asset, p.Quantity }).FirstOrDefaultAsync(cancellationToken);
        return position is null ? AuditTarget.None : await ManagerAsync(position.ManagerAccountId, cancellationToken, $"{position.Type} {position.Asset} {position.Quantity:0.########}");
    }

    public async Task<AuditTarget> CashFlowAsync(long flowId, CancellationToken cancellationToken)
    {
        var flow = await database.CashFlows.Where(f => f.Id == flowId).Select(f => new { f.ManagerAccountId, f.Date, f.AmountUsd }).FirstOrDefaultAsync(cancellationToken);
        return flow is null ? AuditTarget.None : await ManagerAsync(flow.ManagerAccountId, cancellationToken, $"cash flow of {flow.AmountUsd:N2} USD on {flow.Date:yyyy-MM-dd}");
    }

    public async Task<AuditTarget> RuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        var rule = await database.AlertRules.Where(r => r.Id == ruleId).Select(r => new { r.FundId, Fund = r.Fund.Name, r.Metric, r.Operator, r.Threshold, r.ScopeKind }).FirstOrDefaultAsync(cancellationToken);
        return rule is null
            ? AuditTarget.None
            : new AuditTarget(rule.FundId, rule.Fund, $"{rule.ScopeKind} rule: {Alerts.AlertText.Label(rule.Metric, null)} {Alerts.AlertText.Condition(rule.Metric, rule.Operator, rule.Threshold)}");
    }

    public async Task<AuditTarget> AlertAsync(int alertId, CancellationToken cancellationToken)
    {
        int? ruleId = await database.Alerts.Where(a => a.Id == alertId).Select(a => (int?)a.RuleId).FirstOrDefaultAsync(cancellationToken);
        if (ruleId is null)
        {
            return AuditTarget.None;
        }

        AuditTarget rule = await RuleAsync(ruleId.Value, cancellationToken);
        return rule with { Subject = $"Alert {alertId} on {rule.Subject}" };
    }

    public async Task<AuditTarget> ScheduleAsync(int scheduleId, CancellationToken cancellationToken)
    {
        var schedule = await database.ReportSchedules.Where(s => s.Id == scheduleId).Select(s => new { s.FundId, Fund = s.Fund.Name, s.Kind, s.Frequency, s.Recipients }).FirstOrDefaultAsync(cancellationToken);
        return schedule is null ? AuditTarget.None : new AuditTarget(schedule.FundId, schedule.Fund, $"{schedule.Frequency} {schedule.Kind} to {schedule.Recipients}");
    }

    public async Task<AuditTarget> TradingAccountAsync(int accountId, CancellationToken cancellationToken)
    {
        var account = await database.TradingAccounts.Where(a => a.Id == accountId).Select(a => new { a.ManagerAccountId, a.Venue, a.Label }).FirstOrDefaultAsync(cancellationToken);
        return account is null ? AuditTarget.None : await ManagerAsync(account.ManagerAccountId, cancellationToken, $"trading account on {Risk.VenueExposureService.NameOf(account.Venue)} \"{account.Label}\"");
    }

    public async Task<AuditTarget> ApprovalAsync(int requestId, CancellationToken cancellationToken)
    {
        var request = await database.ApprovalRequests.Where(r => r.Id == requestId).Select(r => new { r.FundId, r.Summary, r.RequestedBy }).FirstOrDefaultAsync(cancellationToken);
        if (request is null)
        {
            return AuditTarget.None;
        }

        AuditTarget fund = request.FundId is int fundId ? await FundAsync(fundId, cancellationToken) : AuditTarget.None;
        return fund with { Subject = $"{request.Summary} (asked for by {request.RequestedBy})" };
    }

    public async Task<AuditTarget> OrderAsync(long orderId, CancellationToken cancellationToken)
    {
        var order = await database.Orders.Where(o => o.Id == orderId).Select(o => new { o.TradingAccountId, o.Side, o.Quantity, o.Asset, o.InstrumentType }).FirstOrDefaultAsync(cancellationToken);
        if (order is null)
        {
            return AuditTarget.None;
        }

        AuditTarget account = await TradingAccountAsync(order.TradingAccountId, cancellationToken);
        return account with { Subject = $"{account.Subject} / order {orderId}: {order.Side} {order.Quantity:0.########} {order.Asset} {order.InstrumentType}" };
    }

    private static string? Cut(string? text, int length) => text is null || text.Length <= length ? text : text[..length];
}

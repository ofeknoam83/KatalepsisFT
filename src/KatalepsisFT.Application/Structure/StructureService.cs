using FluentValidation;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Funds;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Structure;

public sealed record NewManagerRequest(string Name, decimal AllocatedCapitalUsd);

public sealed record NewSleeveRequest(string Name, MandateType MandateType, IReadOnlyList<NewManagerRequest> Managers);

/// <param name="Kind">A pooled fund, or a separately managed account of one owner.</param>
/// <param name="OwnerName">For a separately managed account: whose money it is.</param>
public sealed record NewFundRequest(string Name, int? BenchmarkId, IReadOnlyList<NewSleeveRequest> Sleeves, FundKind Kind = FundKind.Fund, string? OwnerName = null);

public sealed record FundCreatedDto(int Id, string Name);

public sealed record LimitDto(MandateLimitKind Kind, decimal Value);

/// <param name="RetiredOn">The day the manager was retired; null while it is active.</param>
public sealed record StructureManagerDto(int Id, string Name, decimal AllocatedCapitalUsd, ManagerDataSource DataSource, bool HasHistory, int Connections, DateOnly? RetiredOn = null);

public sealed record MoveManagerRequest(int SleeveId);

public sealed record StructureSleeveDto(int Id, string Name, MandateType MandateType, IReadOnlyList<LimitDto> Limits, IReadOnlyList<StructureManagerDto> Managers);

public sealed record BenchmarkOptionDto(int Id, string Name);

/// <param name="CanEdit">False for the built-in example fund, which is rebuilt from its definition.</param>
public sealed record StructureDto(int Id, string Name, int? BenchmarkId, bool CanEdit, IReadOnlyList<BenchmarkOptionDto> Benchmarks, IReadOnlyList<StructureSleeveDto> Sleeves, FundKind Kind = FundKind.Fund, string? OwnerName = null);

public sealed record SaveFundRequest(string Name, int? BenchmarkId);

/// <param name="Limits">The sleeve's whole mandate: a kind left out is a limit removed. Null leaves the mandate as it is.</param>
public sealed record SaveSleeveRequest(string Name, MandateType MandateType, IReadOnlyList<LimitDto>? Limits);

public sealed record SaveManagerRequest(string Name, decimal AllocatedCapitalUsd);

public sealed class SaveFundRequestValidator : AbstractValidator<SaveFundRequest>
{
    public SaveFundRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class SaveManagerRequestValidator : AbstractValidator<SaveManagerRequest>
{
    public SaveManagerRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.AllocatedCapitalUsd).InclusiveBetween(0m, 1_000_000_000_000m);
    }
}

public sealed class SaveSleeveRequestValidator : AbstractValidator<SaveSleeveRequest>
{
    public SaveSleeveRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.MandateType).IsInEnum();
        When(
            x => x.Limits is not null,
            () =>
            {
                RuleFor(x => x.Limits!).Must(l => l.Select(x => x.Kind).Distinct().Count() == l.Count).WithMessage("Each limit can be set once.");
                RuleForEach(x => x.Limits!)
                    .ChildRules(limit =>
                    {
                        limit.RuleFor(l => l.Kind).IsInEnum();
                        // Exposure limits are fractions of sleeve NAV (10 = 1,000%).
                        limit.RuleFor(l => l.Value).InclusiveBetween(0m, 10m).When(l => l.Kind is MandateLimitKind.GrossExposureMax or MandateLimitKind.SingleAssetMax).WithMessage("Gross and single-asset limits must be between 0% and 1,000%.");
                        limit.RuleFor(l => l.Value).InclusiveBetween(-10m, 10m).When(l => l.Kind is MandateLimitKind.NetExposureMin or MandateLimitKind.NetExposureMax).WithMessage("Net exposure limits must be between −1,000% and 1,000%.");
                        limit.RuleFor(l => l.Value).InclusiveBetween(-1m, 0m).When(l => l.Kind == MandateLimitKind.ManagerDrawdownMin).WithMessage("A drawdown limit is negative: between −100% and 0%.");
                        limit.RuleFor(l => l.Value).InclusiveBetween(0m, 3650m).When(l => l.Kind == MandateLimitKind.DaysToLiquidateMax).WithMessage("Days to liquidate must be between 0 and 3,650.");
                    });
                RuleFor(x => x.Limits!)
                    .Must(l => l.FirstOrDefault(x => x.Kind == MandateLimitKind.NetExposureMin) is not { } min || l.FirstOrDefault(x => x.Kind == MandateLimitKind.NetExposureMax) is not { } max || min.Value <= max.Value)
                    .WithMessage("The lowest net exposure cannot be above the highest.");
            }
        );
    }
}

/// <summary>Outcome of a structure change: done, refused with a reason, or not found (both false and null).</summary>
/// <param name="Id">What was created, when something was.</param>
public sealed record StructureChange(bool Done, string? Error, int? Id = null)
{
    public static StructureChange Ok { get; } = new(true, null);

    public static StructureChange NotFound { get; } = new(false, null);

    public static StructureChange Refused(string error) => new(false, error);
}

public sealed class NewFundRequestValidator : AbstractValidator<NewFundRequest>
{
    public NewFundRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Sleeves).NotEmpty().WithMessage("A fund needs at least one sleeve.");
        RuleFor(x => x.Sleeves).Must(s => s.Select(x => x.Name.Trim().ToUpperInvariant()).Distinct().Count() == s.Count).WithMessage("Sleeve names must differ.");
        RuleForEach(x => x.Sleeves)
            .ChildRules(sleeve =>
            {
                sleeve.RuleFor(s => s.Name).NotEmpty().MaximumLength(200);
                sleeve.RuleFor(s => s.MandateType).IsInEnum();
                sleeve.RuleFor(s => s.Managers).NotEmpty().WithMessage("A sleeve needs at least one manager.");
                sleeve
                    .RuleForEach(s => s.Managers)
                    .ChildRules(manager =>
                    {
                        manager.RuleFor(m => m.Name).NotEmpty().MaximumLength(200);
                        manager.RuleFor(m => m.AllocatedCapitalUsd).InclusiveBetween(0m, 1_000_000_000_000m);
                    });
            });
    }
}

/// <summary>Creates and edits real funds: their sleeves, mandates and managers. Managers take positions from venue connections.</summary>
public sealed class StructureService(IFtDbContext database, AlertEvaluator alerts, TimeProvider clock)
{
    private const string DemoIsFixed = "The example fund is rebuilt from its definition and cannot be edited.";

    public async Task<StructureDto?> GetAsync(int fundId, CancellationToken cancellationToken)
    {
        Fund? fund = await database.Funds.AsNoTracking().Include(f => f.Sleeves).ThenInclude(s => s.Limits).Include(f => f.Sleeves).ThenInclude(s => s.Managers).FirstOrDefaultAsync(f => f.Id == fundId, cancellationToken);
        if (fund is null)
        {
            return null;
        }

        int[] managerIds = fund.Sleeves.SelectMany(s => s.Managers).Select(m => m.Id).ToArray();
        HashSet<int> valued = (await database.NavSnapshots.Where(n => managerIds.Contains(n.ManagerAccountId)).Select(n => n.ManagerAccountId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        Dictionary<int, int> connections = await database.VenueConnections.Where(c => managerIds.Contains(c.ManagerAccountId)).GroupBy(c => c.ManagerAccountId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        return new StructureDto(
            fund.Id,
            fund.Name,
            fund.BenchmarkId,
            !fund.IsDemo,
            await database.Benchmarks.OrderBy(b => b.Id).Select(b => new BenchmarkOptionDto(b.Id, b.Name)).ToListAsync(cancellationToken),
            fund.Sleeves.OrderBy(s => s.SortOrder)
                .ThenBy(s => s.Id)
                .Select(s => new StructureSleeveDto(
                    s.Id,
                    s.Name,
                    s.MandateType,
                    s.Limits.OrderBy(l => l.Kind).Select(l => new LimitDto(l.Kind, l.Value)).ToList(),
                    s.Managers.OrderBy(m => m.Id).Select(m => new StructureManagerDto(m.Id, m.Name, m.AllocatedCapitalUsd, m.DataSource, valued.Contains(m.Id), connections.GetValueOrDefault(m.Id), m.RetiredOn)).ToList()
                ))
                .ToList(),
            fund.Kind,
            fund.OwnerName
        );
    }

    public async Task<StructureChange> SaveFundAsync(int fundId, SaveFundRequest request, CancellationToken cancellationToken)
    {
        Fund? fund = await database.Funds.FirstOrDefaultAsync(f => f.Id == fundId, cancellationToken);
        if (fund is null)
        {
            return StructureChange.NotFound;
        }

        string name = request.Name.Trim();
        if (fund.IsDemo)
        {
            return StructureChange.Refused(DemoIsFixed);
        }

        if (await database.Funds.AnyAsync(f => f.Id != fundId && f.Name == name, cancellationToken))
        {
            return StructureChange.Refused("A fund with that name already exists.");
        }

        if (request.BenchmarkId is int benchmarkId && !await database.Benchmarks.AnyAsync(b => b.Id == benchmarkId, cancellationToken))
        {
            return StructureChange.Refused("That benchmark does not exist.");
        }

        fund.Name = name;
        fund.BenchmarkId = request.BenchmarkId;
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    public async Task<StructureChange> AddSleeveAsync(int fundId, SaveSleeveRequest request, CancellationToken cancellationToken)
    {
        Fund? fund = await database.Funds.Include(f => f.Sleeves).FirstOrDefaultAsync(f => f.Id == fundId, cancellationToken);
        if (fund is null)
        {
            return StructureChange.NotFound;
        }

        string name = request.Name.Trim();
        if (fund.IsDemo)
        {
            return StructureChange.Refused(DemoIsFixed);
        }

        if (fund.Sleeves.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return StructureChange.Refused("This fund already has a sleeve with that name.");
        }

        fund.Sleeves.Add(
            new Sleeve
            {
                Name = name,
                MandateType = request.MandateType,
                SortOrder = fund.Sleeves.Count == 0 ? 1 : fund.Sleeves.Max(s => s.SortOrder) + 1,
                Limits = (request.Limits ?? []).Select(l => new MandateLimit { Kind = l.Kind, Value = l.Value }).ToList(),
            }
        );
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    /// <summary>Renames a sleeve and, when limits are given, makes its mandate exactly those limits. Alert rules follow at once.</summary>
    public async Task<StructureChange> SaveSleeveAsync(int sleeveId, SaveSleeveRequest request, CancellationToken cancellationToken)
    {
        Sleeve? sleeve = await database.Sleeves.Include(s => s.Fund).Include(s => s.Limits).FirstOrDefaultAsync(s => s.Id == sleeveId, cancellationToken);
        if (sleeve is null)
        {
            return StructureChange.NotFound;
        }

        string name = request.Name.Trim();
        if (sleeve.Fund.IsDemo)
        {
            return StructureChange.Refused(DemoIsFixed);
        }

        if (await database.Sleeves.AnyAsync(s => s.FundId == sleeve.FundId && s.Id != sleeveId && s.Name.ToLower() == name.ToLower(), cancellationToken))
        {
            return StructureChange.Refused("This fund already has a sleeve with that name.");
        }

        sleeve.Name = name;
        sleeve.MandateType = request.MandateType;
        if (request.Limits is not null)
        {
            // A limit that stays keeps its row, and with it its alert rule and that rule's history.
            foreach (MandateLimit gone in sleeve.Limits.Where(l => request.Limits.All(r => r.Kind != l.Kind)).ToList())
            {
                sleeve.Limits.Remove(gone);
                database.MandateLimits.Remove(gone);
            }

            foreach (LimitDto limit in request.Limits)
            {
                MandateLimit? existing = sleeve.Limits.FirstOrDefault(l => l.Kind == limit.Kind);
                if (existing is null)
                {
                    sleeve.Limits.Add(new MandateLimit { Kind = limit.Kind, Value = limit.Value });
                }
                else
                {
                    existing.Value = limit.Value;
                }
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        await alerts.EvaluateAsync(sleeve.FundId, cancellationToken);
        return StructureChange.Ok;
    }

    /// <summary>A sleeve can go only while it is empty, so no manager's history is lost with it.</summary>
    public async Task<StructureChange> DeleteSleeveAsync(int sleeveId, CancellationToken cancellationToken)
    {
        Sleeve? sleeve = await database.Sleeves.Include(s => s.Fund).Include(s => s.Managers).FirstOrDefaultAsync(s => s.Id == sleeveId, cancellationToken);
        if (sleeve is null)
        {
            return StructureChange.NotFound;
        }

        if (sleeve.Fund.IsDemo)
        {
            return StructureChange.Refused(DemoIsFixed);
        }

        if (sleeve.Managers.Count > 0)
        {
            return StructureChange.Refused("Remove the sleeve's managers first.");
        }

        database.Sleeves.Remove(sleeve);
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    public async Task<StructureChange> AddManagerAsync(int sleeveId, SaveManagerRequest request, CancellationToken cancellationToken)
    {
        Sleeve? sleeve = await database.Sleeves.Include(s => s.Fund).FirstOrDefaultAsync(s => s.Id == sleeveId, cancellationToken);
        if (sleeve is null)
        {
            return StructureChange.NotFound;
        }

        if (sleeve.Fund.IsDemo)
        {
            return StructureChange.Refused(DemoIsFixed);
        }

        string name = request.Name.Trim();
        if (await database.ManagerAccounts.AnyAsync(m => m.SleeveId == sleeveId && m.Name.ToLower() == name.ToLower(), cancellationToken))
        {
            return StructureChange.Refused("This sleeve already has a manager with that name.");
        }

        ManagerAccount manager = new()
        {
            SleeveId = sleeveId,
            Name = name,
            AllocatedCapitalUsd = request.AllocatedCapitalUsd,
            InceptionDate = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
            DataSource = ManagerDataSource.Venue,
        };
        database.ManagerAccounts.Add(manager);
        await database.SaveChangesAsync(cancellationToken);
        return new StructureChange(true, null, manager.Id);
    }

    public async Task<StructureChange> SaveManagerAsync(int managerId, SaveManagerRequest request, CancellationToken cancellationToken)
    {
        ManagerAccount? manager = await database.ManagerAccounts.Include(m => m.Sleeve).ThenInclude(s => s.Fund).FirstOrDefaultAsync(m => m.Id == managerId, cancellationToken);
        if (manager is null)
        {
            return StructureChange.NotFound;
        }

        if (manager.Sleeve.Fund.IsDemo)
        {
            return StructureChange.Refused(DemoIsFixed);
        }

        manager.Name = request.Name.Trim();
        manager.AllocatedCapitalUsd = request.AllocatedCapitalUsd;
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    /// <summary>
    /// Puts a manager in another sleeve of the same fund. Its whole record goes with it: sleeve figures are always
    /// those of the managers a sleeve has now, so the old sleeve's past no longer includes it and the new one's does.
    /// </summary>
    public async Task<StructureChange> MoveManagerAsync(int managerId, int sleeveId, CancellationToken cancellationToken)
    {
        ManagerAccount? manager = await database.ManagerAccounts.Include(m => m.Sleeve).ThenInclude(s => s.Fund).FirstOrDefaultAsync(m => m.Id == managerId, cancellationToken);
        Sleeve? target = await database.Sleeves.FirstOrDefaultAsync(s => s.Id == sleeveId, cancellationToken);
        if (manager is null || target is null)
        {
            return StructureChange.NotFound;
        }

        if (manager.Sleeve.Fund.IsDemo)
        {
            return StructureChange.Refused(DemoIsFixed);
        }

        if (target.FundId != manager.Sleeve.FundId)
        {
            return StructureChange.Refused("A manager can only move to a sleeve of the same fund.");
        }

        if (target.Id == manager.SleeveId)
        {
            return StructureChange.Ok;
        }

        if (await database.ManagerAccounts.AnyAsync(m => m.SleeveId == sleeveId && m.Name.ToLower() == manager.Name.ToLower(), cancellationToken))
        {
            return StructureChange.Refused($"{target.Name} already has a manager with that name.");
        }

        int fundId = manager.Sleeve.FundId;
        manager.SleeveId = sleeveId;
        await database.SaveChangesAsync(cancellationToken);
        // Both sleeves' limits now apply to different books.
        await alerts.EvaluateAsync(fundId, cancellationToken);
        return StructureChange.Ok;
    }

    /// <summary>
    /// Closes a manager without rewriting the past. From today its book is empty and nothing reads it: its keys and
    /// wallets are switched off and its hand-entered positions no longer count. What it was worth leaves the fund as
    /// money going out, so neither the fund's nor the sleeve's return shows a loss, and everything up to today stays.
    /// </summary>
    public async Task<StructureChange> RetireManagerAsync(int managerId, CancellationToken cancellationToken)
    {
        ManagerAccount? manager = await database.ManagerAccounts.Include(m => m.Sleeve).ThenInclude(s => s.Fund).FirstOrDefaultAsync(m => m.Id == managerId, cancellationToken);
        if (manager is null)
        {
            return StructureChange.NotFound;
        }

        if (manager.Sleeve.Fund.IsDemo)
        {
            return StructureChange.Refused(DemoIsFixed);
        }

        if (!manager.IsActive)
        {
            return StructureChange.Ok;
        }

        DateTimeOffset now = clock.GetUtcNow();
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        int fundId = manager.Sleeve.FundId;
        if (await database.NavSnapshots.AnyAsync(n => n.ManagerAccountId == managerId, cancellationToken))
        {
            // One last valuation, so what leaves is what the book is worth now and not what it was at the last read.
            (decimal nav, _, _) = await new ManagerBookWriter(database).WriteAsync(managerId, today, cancellationToken);
            manager = await database.ManagerAccounts.FirstAsync(m => m.Id == managerId, cancellationToken);
            database.PositionSnapshots.RemoveRange(await database.PositionSnapshots.Where(p => p.ManagerAccountId == managerId && p.Date == today).ToListAsync(cancellationToken));
            (await database.NavSnapshots.FirstAsync(n => n.ManagerAccountId == managerId && n.Date == today, cancellationToken)).NavUsd = 0m;
            if (nav != 0m)
            {
                database.CashFlows.Add(ConnectionSyncService.BookChange(managerId, -nav, now, "Manager retired: its book leaves the fund"));
            }
        }

        foreach (VenueConnection connection in await database.VenueConnections.Where(c => c.ManagerAccountId == managerId && c.State != ConnectionState.Disabled).ToListAsync(cancellationToken))
        {
            connection.State = ConnectionState.Disabled;
        }

        // The book left as one sum just above. Its trading accounts are not counted any more, and any working
        // order still open for the manager is told to stop: a retired manager trades nothing.
        foreach (Domain.Execution.TradingAccount account in await database.TradingAccounts.Where(a => a.ManagerAccountId == managerId && a.InBookEquityUsd != null).ToListAsync(cancellationToken))
        {
            account.InBookEquityUsd = null;
            account.InBookMode = null;
        }

        foreach (Domain.Execution.WorkingOrder working in await database.WorkingOrders.Where(w => w.ManagerAccountId == managerId && (w.State == Domain.Execution.WorkingState.Working || w.State == Domain.Execution.WorkingState.Paused || w.State == Domain.Execution.WorkingState.PendingApproval)).ToListAsync(cancellationToken))
        {
            if (working.State == Domain.Execution.WorkingState.PendingApproval)
            {
                working.State = Domain.Execution.WorkingState.Stopped;
                working.EndedAt = now;
            }
            else
            {
                working.StopRequested = true;
            }

            working.Note = "Stopped: its manager has been retired.";
        }

        manager.IsActive = false;
        manager.RetiredOn = today;
        await database.SaveChangesAsync(cancellationToken);
        await alerts.EvaluateAsync(fundId, cancellationToken);
        return StructureChange.Ok;
    }

    /// <summary>
    /// Opens a retired manager again. Its hand-entered positions count from today and come back in as money
    /// arriving; its keys and wallets stay off until each is switched on, which brings that account in the same way.
    /// </summary>
    public async Task<StructureChange> ReinstateManagerAsync(int managerId, CancellationToken cancellationToken)
    {
        ManagerAccount? manager = await database.ManagerAccounts.Include(m => m.Sleeve).ThenInclude(s => s.Fund).FirstOrDefaultAsync(m => m.Id == managerId, cancellationToken);
        if (manager is null)
        {
            return StructureChange.NotFound;
        }

        if (manager.IsActive)
        {
            return StructureChange.Ok;
        }

        int fundId = manager.Sleeve.FundId;
        manager.IsActive = true;
        manager.RetiredOn = null;
        await database.SaveChangesAsync(cancellationToken);
        if (await database.ManualPositions.AnyAsync(p => p.ManagerAccountId == managerId, cancellationToken))
        {
            DateTimeOffset now = clock.GetUtcNow();
            (decimal nav, _, _) = await new ManagerBookWriter(database).WriteAsync(managerId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);
            // Trading accounts that came back with this write recorded their own arrival; what is left is the rest of the book.
            nav -= await database.TradingAccounts.Where(a => a.ManagerAccountId == managerId && a.InBookEquityUsd != null).SumAsync(a => a.InBookEquityUsd ?? 0m, cancellationToken);
            if (nav != 0m)
            {
                database.CashFlows.Add(ConnectionSyncService.BookChange(managerId, nav, now, "Manager reinstated: its book comes back into the fund"));
                await database.SaveChangesAsync(cancellationToken);
            }
        }

        await alerts.EvaluateAsync(fundId, cancellationToken);
        return StructureChange.Ok;
    }

    /// <summary>A manager can go only before it has any history or venue key; afterwards removing it would rewrite the fund's past.</summary>
    public async Task<StructureChange> DeleteManagerAsync(int managerId, CancellationToken cancellationToken)
    {
        ManagerAccount? manager = await database.ManagerAccounts.Include(m => m.Sleeve).ThenInclude(s => s.Fund).FirstOrDefaultAsync(m => m.Id == managerId, cancellationToken);
        if (manager is null)
        {
            return StructureChange.NotFound;
        }

        if (manager.Sleeve.Fund.IsDemo)
        {
            return StructureChange.Refused(DemoIsFixed);
        }

        if (await database.VenueConnections.AnyAsync(c => c.ManagerAccountId == managerId, cancellationToken))
        {
            return StructureChange.Refused("Remove the manager's venue keys first.");
        }

        if (await database.NavSnapshots.AnyAsync(n => n.ManagerAccountId == managerId, cancellationToken))
        {
            return StructureChange.Refused("This manager has history in the fund's performance and cannot be removed.");
        }

        database.ManagerAccounts.Remove(manager);
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    /// <returns>The new fund, or the reason it was refused.</returns>
    public async Task<(FundCreatedDto? Fund, string? Error)> CreateFundAsync(NewFundRequest request, CancellationToken cancellationToken)
    {
        string name = request.Name.Trim();
        if (await database.Funds.AnyAsync(f => f.Name == name, cancellationToken))
        {
            return (null, "A fund with that name already exists.");
        }

        if (request.BenchmarkId is int benchmarkId && !await database.Benchmarks.AnyAsync(b => b.Id == benchmarkId, cancellationToken))
        {
            return (null, "That benchmark does not exist.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        if (request.Kind == FundKind.Sma && string.IsNullOrWhiteSpace(request.OwnerName))
        {
            return (null, "A separately managed account needs its owner's name.");
        }

        Fund fund = new()
        {
            Name = name,
            Kind = request.Kind,
            OwnerName = request.Kind == FundKind.Sma ? request.OwnerName!.Trim() : null,
            BenchmarkId = request.BenchmarkId,
            CreatedAt = now,
            Sleeves = request
                .Sleeves.Select(
                    (sleeve, index) =>
                        new Sleeve
                        {
                            Name = sleeve.Name.Trim(),
                            MandateType = sleeve.MandateType,
                            SortOrder = index + 1,
                            Managers = sleeve
                                .Managers.Select(m => new ManagerAccount
                                {
                                    Name = m.Name.Trim(),
                                    AllocatedCapitalUsd = m.AllocatedCapitalUsd,
                                    InceptionDate = today,
                                    DataSource = ManagerDataSource.Venue,
                                })
                                .ToList(),
                        }
                )
                .ToList(),
        };
        database.Funds.Add(fund);
        await database.SaveChangesAsync(cancellationToken);
        return (new FundCreatedDto(fund.Id, fund.Name), null);
    }
}

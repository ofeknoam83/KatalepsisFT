using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Funds;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Structure;

public sealed record RenameRequest(string Name);

/// <summary>
/// Changing what something is called, and nothing else about it: a fund or separately managed account, its owner,
/// a sleeve, a manager, a key or wallet, a trading account, a strategy key. History, figures and alert rules stay
/// with the thing; only the name on them changes.
/// </summary>
public sealed class RenameService(IFtDbContext database)
{
    private const string DemoIsFixed = "The example fund is rebuilt from its definition and cannot be changed.";

    public async Task<StructureChange> FundAsync(int fundId, string? name, CancellationToken cancellationToken)
    {
        Fund? fund = await database.Funds.FirstOrDefaultAsync(f => f.Id == fundId, cancellationToken);
        if (fund is null)
        {
            return StructureChange.NotFound;
        }

        if (Check(name, 200, fund.IsDemo) is { } refusal)
        {
            return refusal;
        }

        string wanted = name!.Trim();
        if (await database.Funds.AnyAsync(f => f.Id != fundId && f.Name.ToLower() == wanted.ToLower(), cancellationToken))
        {
            return StructureChange.Refused("A fund with that name already exists.");
        }

        fund.Name = wanted;
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    /// <summary>Whose money a separately managed account is.</summary>
    public async Task<StructureChange> FundOwnerAsync(int fundId, string? name, CancellationToken cancellationToken)
    {
        Fund? fund = await database.Funds.FirstOrDefaultAsync(f => f.Id == fundId, cancellationToken);
        if (fund is null)
        {
            return StructureChange.NotFound;
        }

        if (fund.Kind != FundKind.Sma)
        {
            return StructureChange.Refused("Only a separately managed account has an owner.");
        }

        if (Check(name, 200, fund.IsDemo) is { } refusal)
        {
            return refusal;
        }

        fund.OwnerName = name!.Trim();
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    public async Task<StructureChange> SleeveAsync(int sleeveId, string? name, CancellationToken cancellationToken)
    {
        Sleeve? sleeve = await database.Sleeves.Include(s => s.Fund).FirstOrDefaultAsync(s => s.Id == sleeveId, cancellationToken);
        if (sleeve is null)
        {
            return StructureChange.NotFound;
        }

        if (Check(name, 200, sleeve.Fund.IsDemo) is { } refusal)
        {
            return refusal;
        }

        string wanted = name!.Trim();
        if (await database.Sleeves.AnyAsync(s => s.FundId == sleeve.FundId && s.Id != sleeveId && s.Name.ToLower() == wanted.ToLower(), cancellationToken))
        {
            return StructureChange.Refused("This fund already has a sleeve with that name.");
        }

        sleeve.Name = wanted;
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    public async Task<StructureChange> ManagerAsync(int managerId, string? name, CancellationToken cancellationToken)
    {
        ManagerAccount? manager = await database.ManagerAccounts.Include(m => m.Sleeve).ThenInclude(s => s.Fund).FirstOrDefaultAsync(m => m.Id == managerId, cancellationToken);
        if (manager is null)
        {
            return StructureChange.NotFound;
        }

        if (Check(name, 200, manager.Sleeve.Fund.IsDemo) is { } refusal)
        {
            return refusal;
        }

        manager.Name = name!.Trim();
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    public async Task<StructureChange> ConnectionAsync(int connectionId, string? label, CancellationToken cancellationToken)
    {
        VenueConnection? connection = await database.VenueConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return StructureChange.NotFound;
        }

        if (Check(label, 100, false) is { } refusal)
        {
            return refusal;
        }

        connection.Label = label!.Trim();
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    public async Task<StructureChange> TradingAccountAsync(int accountId, string? label, CancellationToken cancellationToken)
    {
        TradingAccount? account = await database.TradingAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return StructureChange.NotFound;
        }

        if (Check(label, 100, false) is { } refusal)
        {
            return refusal;
        }

        account.Label = label!.Trim();
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    public async Task<StructureChange> StrategyKeyAsync(int keyId, string? name, CancellationToken cancellationToken)
    {
        StrategyKey? key = await database.StrategyKeys.FirstOrDefaultAsync(k => k.Id == keyId, cancellationToken);
        if (key is null)
        {
            return StructureChange.NotFound;
        }

        if (Check(name, 100, false) is { } refusal)
        {
            return refusal;
        }

        key.Name = name!.Trim();
        await database.SaveChangesAsync(cancellationToken);
        return StructureChange.Ok;
    }

    private static StructureChange? Check(string? name, int longest, bool isDemo) =>
        isDemo ? StructureChange.Refused(DemoIsFixed)
        : string.IsNullOrWhiteSpace(name) ? StructureChange.Refused("Give a name.")
        : name.Trim().Length > longest ? StructureChange.Refused($"A name can be up to {longest} characters.")
        : null;
}

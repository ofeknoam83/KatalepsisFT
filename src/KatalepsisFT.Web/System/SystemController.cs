using KatalepsisFT.Domain.Access;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Infrastructure.Health;
using KatalepsisFT.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Web.System;

/// <summary>
/// The state of the controls that protect money, counted from the database at the moment of asking. Nothing here is
/// a secret: it is how many keys there are and what they are allowed to do, never a key.
/// </summary>
/// <param name="ReadKeys">Read-only venue keys that are switched on.</param>
/// <param name="ReadKeysConfirmed">Of those, how many the venue confirmed cannot trade or withdraw.</param>
/// <param name="Wallets">Wallets followed by their public address; there is no key at all.</param>
/// <param name="TradingKeys">Trading accounts that hold a sealed trading key.</param>
/// <param name="StrategyKeys">Keys that let a program send orders, switched on.</param>
/// <param name="KillSwitchesPulled">Kill switches pulled and not yet released.</param>
/// <param name="ApprovesAlone">Administrators allowed to approve their own requests.</param>
public sealed record SecurityReport(
    int ReadKeys,
    int ReadKeysConfirmed,
    int Wallets,
    int PaperAccounts,
    int LiveAccounts,
    int TradingKeys,
    int StrategyKeys,
    int KillSwitchesPulled,
    int ApprovalsWaiting,
    int ApprovalsDecided,
    int Users,
    int Administrators,
    int ApprovesAlone,
    long AuditEntries,
    DateTimeOffset? LastAuditEntryAt
);

[ApiController]
[Route("api/system")]
[Authorize(Roles = Roles.Admin)]
public sealed class SystemController(ReadinessProbe probe, FtDbContext database) : ControllerBase
{
    private static readonly Venue[] WalletVenues = [Venue.EvmWallet, Venue.BitcoinWallet, Venue.SolanaWallet, Venue.TronWallet];

    /// <summary>What this instance can reach, and when it last heard each worker.</summary>
    [HttpGet("status")]
    public async Task<ReadinessReport> StatusAsync(CancellationToken cancellationToken) => await probe.CheckAsync(cancellationToken);

    /// <summary>How the controls stand right now: keys and what they may do, kill switches, approvals, people, the record.</summary>
    [HttpGet("security")]
    public async Task<SecurityReport> SecurityAsync(CancellationToken cancellationToken)
    {
        var connections = await database
            .VenueConnections.AsNoTracking()
            .Where(c => c.State != ConnectionState.Disabled)
            .Select(c => new { c.Venue, c.PermissionsVerified })
            .ToListAsync(cancellationToken);
        var accounts = await database.TradingAccounts.AsNoTracking().Select(a => new { a.Mode, HasKey = a.EncryptedKey != null }).ToListAsync(cancellationToken);
        int adminRole = await database.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleOrDefaultAsync(cancellationToken);
        var keys = connections.Where(c => !WalletVenues.Contains(c.Venue)).ToList();
        return new SecurityReport(
            keys.Count,
            keys.Count(c => c.PermissionsVerified),
            connections.Count - keys.Count,
            accounts.Count(a => a.Mode == TradingMode.Paper),
            accounts.Count(a => a.Mode != TradingMode.Paper),
            accounts.Count(a => a.HasKey),
            await database.StrategyKeys.CountAsync(k => k.IsEnabled, cancellationToken),
            await database.TradingHalts.CountAsync(h => h.ReleasedAt == null, cancellationToken),
            await database.ApprovalRequests.CountAsync(r => r.Status == ApprovalStatus.Pending, cancellationToken),
            await database.ApprovalRequests.CountAsync(r => r.Status != ApprovalStatus.Pending, cancellationToken),
            await database.Users.CountAsync(cancellationToken),
            await database.UserRoles.CountAsync(r => r.RoleId == adminRole, cancellationToken),
            await database.Users.CountAsync(u => u.ApprovesAlone, cancellationToken),
            await database.AuditEntries.LongCountAsync(cancellationToken),
            await database.AuditEntries.MaxAsync(e => (DateTimeOffset?)e.At, cancellationToken)
        );
    }
}

using System.Text.Json;
using FluentValidation;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Funds;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Connections;

public sealed record VenueOptionDto(Venue Venue, string Name, bool NeedsPassphrase, bool UsesAddress);

/// <param name="KeyHint">The last four characters of the API key. The key itself is never sent back.</param>
/// <param name="ComputedNavUsd">What this connection adds to the manager's NAV, from its positions.</param>
/// <param name="ReportedEquityUsd">The account value the venue states. A gap between the two means something is not being read.</param>
public sealed record ConnectionDto(
    int Id,
    int ManagerId,
    string Manager,
    string Sleeve,
    Venue Venue,
    string VenueName,
    string Label,
    string KeyHint,
    bool PermissionsVerified,
    ConnectionState State,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError,
    int Positions,
    decimal? ComputedNavUsd,
    decimal? ReportedEquityUsd,
    string? TransfersError,
    IReadOnlyList<string> Notes
);

public sealed record ConnectionManagerDto(int Id, string Name, string Sleeve);

/// <param name="CanAddKeys">False when the master key is not configured.</param>
public sealed record ConnectionsDto(bool CanAddKeys, IReadOnlyList<VenueOptionDto> Venues, IReadOnlyList<ConnectionManagerDto> Managers, IReadOnlyList<ConnectionDto> Connections);

/// <param name="ApiKey">The API key, or the wallet address for venues that are read by address.</param>
/// <param name="ApiSecret">Empty for venues that are read by address.</param>
public sealed record AddConnectionRequest(int ManagerId, Venue Venue, string Label, string ApiKey, string? ApiSecret, string? Passphrase);

public sealed class AddConnectionRequestValidator : AbstractValidator<AddConnectionRequest>
{
    public AddConnectionRequestValidator()
    {
        RuleFor(x => x.Venue).IsInEnum();
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ApiKey).NotEmpty().MaximumLength(512);
        RuleFor(x => x.ApiSecret).MaximumLength(4096);
        RuleFor(x => x.Passphrase).MaximumLength(512);
    }
}

/// <summary>Outcome of a change to connections: the new state, or why it was refused. Both null means not found.</summary>
public sealed record ConnectionChange(ConnectionDto? Value, string? Error);

/// <summary>Venue keys of a fund's managers: listing, adding after the venue has confirmed the key is read-only, switching off, removing.</summary>
public sealed class ConnectionService(IFtDbContext database, VenueAdapterRegistry adapters, ICredentialVault vault, ConnectionSyncService sync, TimeProvider clock)
{
    public async Task<ConnectionsDto?> ListAsync(int fundId, CancellationToken cancellationToken)
    {
        if (!await database.Funds.AnyAsync(f => f.Id == fundId, cancellationToken))
        {
            return null;
        }

        List<VenueConnection> connections = await database
            .VenueConnections.AsNoTracking()
            .Include(c => c.ManagerAccount)
            .ThenInclude(m => m.Sleeve)
            .Where(c => c.ManagerAccount.Sleeve.FundId == fundId)
            .OrderBy(c => c.ManagerAccount.Sleeve.SortOrder)
            .ThenBy(c => c.ManagerAccountId)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        List<ConnectionManagerDto> managers = await database
            .ManagerAccounts.Where(m => m.Sleeve.FundId == fundId && m.DataSource == ManagerDataSource.Venue)
            .OrderBy(m => m.Sleeve.SortOrder)
            .ThenBy(m => m.Id)
            .Select(m => new ConnectionManagerDto(m.Id, m.Name, m.Sleeve.Name))
            .ToListAsync(cancellationToken);
        return new ConnectionsDto(
            vault.IsAvailable,
            adapters.All.OrderBy(a => a.Venue).Select(a => new VenueOptionDto(a.Venue, a.DisplayName, a.NeedsPassphrase, a.UsesAddress)).ToList(),
            managers,
            connections.Select(Dto).ToList()
        );
    }

    /// <summary>Checks the key with the venue before anything is stored. A key that can trade or withdraw is refused.</summary>
    public async Task<ConnectionChange> AddAsync(AddConnectionRequest request, string createdBy, CancellationToken cancellationToken)
    {
        ManagerAccount? manager = await database.ManagerAccounts.Include(m => m.Sleeve).FirstOrDefaultAsync(m => m.Id == request.ManagerId, cancellationToken);
        if (manager is null)
        {
            return new ConnectionChange(null, null);
        }

        if (manager.DataSource != ManagerDataSource.Venue)
        {
            return new ConnectionChange(null, "This manager's positions do not come from venues.");
        }

        if (!vault.IsAvailable)
        {
            return new ConnectionChange(null, "The master key is not configured on the server, so keys cannot be stored.");
        }

        IVenueAdapter? adapter = adapters.Find(request.Venue);
        if (adapter is null)
        {
            return new ConnectionChange(null, $"{request.Venue} is not supported yet.");
        }

        if (!adapter.UsesAddress && string.IsNullOrWhiteSpace(request.ApiSecret))
        {
            return new ConnectionChange(null, $"{adapter.DisplayName} keys come with a secret.");
        }

        // A wallet address has no secret; whatever was typed there is not kept.
        VenueCredentials credentials = adapter.UsesAddress
            ? new VenueCredentials(request.ApiKey.Trim(), string.Empty)
            : new VenueCredentials(request.ApiKey.Trim(), request.ApiSecret!.Trim(), string.IsNullOrWhiteSpace(request.Passphrase) ? null : request.Passphrase.Trim());
        if (adapter.NeedsPassphrase && credentials.Passphrase is null)
        {
            return new ConnectionChange(null, $"{adapter.DisplayName} keys come with a passphrase.");
        }

        // The same key twice in one fund would count the account twice.
        string hint = credentials.ApiKey.Length <= 4 ? credentials.ApiKey : credentials.ApiKey[^4..];
        int fundId = await database.Sleeves.Where(s => s.Id == manager.SleeveId).Select(s => s.FundId).SingleAsync(cancellationToken);
        foreach (VenueConnection other in await database.VenueConnections.AsNoTracking().Where(c => c.Venue == request.Venue && c.KeyHint == hint && c.ManagerAccount.Sleeve.FundId == fundId).ToListAsync(cancellationToken))
        {
            if (string.Equals(vault.Unprotect(other.EncryptedCredentials).ApiKey, credentials.ApiKey, adapter.UsesAddress ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return new ConnectionChange(null, $"This {(adapter.UsesAddress ? "address" : "key")} is already connected in this fund as \"{other.Label}\".");
            }
        }

        CredentialCheck check;
        try
        {
            check = await adapter.CheckCredentialsAsync(credentials, cancellationToken);
        }
        catch (VenueException e)
        {
            return new ConnectionChange(null, e.Failure == VenueFailure.Rejected ? $"{adapter.DisplayName} refused the {(adapter.UsesAddress ? "address" : "key")}: {e.Message}" : $"{adapter.DisplayName} could not be reached: {e.Message}");
        }

        if (!check.IsReadOnly)
        {
            string can = check is { CanTrade: true, CanWithdraw: true } ? "trade and withdraw" : check.CanTrade == true ? "trade" : "withdraw";
            return new ConnectionChange(null, $"This key can {can}. Create a read-only key on {adapter.DisplayName} and use that instead.");
        }

        VenueConnection connection = new()
        {
            ManagerAccountId = manager.Id,
            ManagerAccount = manager,
            Venue = request.Venue,
            Label = request.Label.Trim(),
            EncryptedCredentials = vault.Protect(credentials),
            KeyHint = credentials.ApiKey.Length <= 4 ? credentials.ApiKey : credentials.ApiKey[^4..],
            PermissionsVerified = check.Verified,
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = createdBy,
        };
        database.VenueConnections.Add(connection);
        await database.SaveChangesAsync(cancellationToken);
        await sync.SyncAsync(connection.Id, cancellationToken);
        return new ConnectionChange(Dto(connection), null);
    }

    public async Task<ConnectionChange> SyncNowAsync(int connectionId, CancellationToken cancellationToken)
    {
        if (!await sync.SyncAsync(connectionId, cancellationToken))
        {
            bool exists = await database.VenueConnections.AnyAsync(c => c.Id == connectionId, cancellationToken);
            return new ConnectionChange(null, exists ? "The connection is switched off." : null);
        }

        return new ConnectionChange(await FindAsync(connectionId, cancellationToken), null);
    }

    /// <summary>Switching off removes the connection's positions from the manager's book; switching on reads it again.</summary>
    public async Task<ConnectionChange> SetEnabledAsync(int connectionId, bool enabled, CancellationToken cancellationToken)
    {
        VenueConnection? connection = await database.VenueConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return new ConnectionChange(null, null);
        }

        if (enabled && connection.State == ConnectionState.Disabled)
        {
            connection.State = ConnectionState.Pending;
            connection.ConsecutiveFailures = 0;
            // What it last read may be months old and must not go back into the book; the first fresh read brings
            // the account in as it is now. Whatever was moved while it was off is already part of that reading.
            connection.LastSnapshotJson = null;
            connection.TransfersCheckedAt = clock.GetUtcNow();
            connection.TransfersError = null;
            await database.SaveChangesAsync(cancellationToken);
            await sync.SyncAsync(connectionId, cancellationToken);
        }
        else if (!enabled && connection.State != ConnectionState.Disabled)
        {
            connection.State = ConnectionState.Disabled;
            RecordLeaving(connection);
            await database.SaveChangesAsync(cancellationToken);
            await RewriteBookAsync(connection.ManagerAccountId, cancellationToken);
        }

        return new ConnectionChange(await FindAsync(connectionId, cancellationToken), null);
    }

    /// <returns>False when the connection does not exist.</returns>
    public async Task<bool> DeleteAsync(int connectionId, CancellationToken cancellationToken)
    {
        VenueConnection? connection = await database.VenueConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return false;
        }

        if (connection.State != ConnectionState.Disabled)
        {
            RecordLeaving(connection);
        }

        database.VenueConnections.Remove(connection);
        await database.SaveChangesAsync(cancellationToken);
        await RewriteBookAsync(connection.ManagerAccountId, cancellationToken);
        return true;
    }

    /// <summary>The value a connection takes out of its manager's book is money leaving, not a loss.</summary>
    private void RecordLeaving(VenueConnection connection)
    {
        if (connection.LastSnapshotJson is null)
        {
            return;
        }

        decimal value = ManagerBookWriter.NavOf(JsonSerializer.Deserialize<VenueSnapshot>(connection.LastSnapshotJson, ManagerBookWriter.Json)!);
        database.CashFlows.Add(ConnectionSyncService.BookChange(connection.ManagerAccountId, -value, clock.GetUtcNow(), $"Connection \"{connection.Label}\" taken out of the book"));
    }

    /// <summary>History is kept; only today's book changes, and only if the manager has been valued before.</summary>
    private async Task RewriteBookAsync(int managerAccountId, CancellationToken cancellationToken)
    {
        if (await database.NavSnapshots.AnyAsync(n => n.ManagerAccountId == managerAccountId, cancellationToken))
        {
            await new ManagerBookWriter(database).WriteAsync(managerAccountId, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), cancellationToken);
        }
    }

    private async Task<ConnectionDto?> FindAsync(int connectionId, CancellationToken cancellationToken)
    {
        VenueConnection? connection = await database.VenueConnections.AsNoTracking().Include(c => c.ManagerAccount).ThenInclude(m => m.Sleeve).FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        return connection is null ? null : Dto(connection);
    }

    private ConnectionDto Dto(VenueConnection c)
    {
        VenueSnapshot? snapshot = c.LastSnapshotJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<VenueSnapshot>(c.LastSnapshotJson, ManagerBookWriter.Json);
        return new ConnectionDto(
            c.Id,
            c.ManagerAccountId,
            c.ManagerAccount.Name,
            c.ManagerAccount.Sleeve.Name,
            c.Venue,
            adapters.Find(c.Venue)?.DisplayName ?? c.Venue.ToString(),
            c.Label,
            c.KeyHint,
            c.PermissionsVerified,
            c.State,
            c.LastAttemptAt,
            c.LastSuccessAt,
            c.LastError,
            snapshot?.Positions.Count ?? 0,
            snapshot is null ? null : ManagerBookWriter.NavOf(snapshot),
            snapshot?.ReportedEquityUsd,
            c.TransfersError,
            snapshot?.Notes ?? []
        );
    }
}

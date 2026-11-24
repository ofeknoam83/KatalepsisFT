using System.Text.Json;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Application.Live;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KatalepsisFT.Application.Connections;

/// <summary>Reads one connection from its venue and, on a complete answer, rewrites the manager's book for today.</summary>
public sealed class ConnectionSyncService(IFtDbContext database, VenueAdapterRegistry adapters, ICredentialVault vault, ManagerBookService books, TimeProvider clock, ILogger<ConnectionSyncService> logger)
{
    /// <summary>Longest wait between attempts on a connection that keeps failing.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    /// <summary>Deposits and withdrawals are read less often than positions; they are rare and cost several calls.</summary>
    public static readonly TimeSpan TransferInterval = TimeSpan.FromMinutes(10);

    /// <summary>Each read looks back a little further than the last, so a transfer completing late is not missed.</summary>
    private static readonly TimeSpan TransferOverlap = TimeSpan.FromHours(6);

    private static readonly HashSet<string> DollarCoins = new(StringComparer.OrdinalIgnoreCase) { "USD", "USDT", "USDC", "DAI", "FDUSD", "TUSD", "USDE", "PYUSD", "USDS", "BUSD", "USDP" };

    /// <summary>When a failing connection may be tried again: 1, 2, 4 … minutes, capped.</summary>
    public static TimeSpan Backoff(int consecutiveFailures) =>
        consecutiveFailures <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(Math.Min(MaxBackoff.TotalMinutes, Math.Pow(2, Math.Min(consecutiveFailures, 10) - 1)));

    /// <param name="paced">True for the worker's reads every few seconds: the fund's alert rules are then checked at a slower pace.</param>
    /// <returns>False when the connection does not exist or is switched off.</returns>
    public async Task<bool> SyncAsync(int connectionId, CancellationToken cancellationToken, bool paced = false)
    {
        VenueConnection? connection = await database.VenueConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null || connection.State == ConnectionState.Disabled)
        {
            return false;
        }

        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset? previousSuccess = connection.LastSuccessAt;
        connection.LastAttemptAt = now;
        try
        {
            IVenueAdapter adapter = adapters.Find(connection.Venue) ?? throw new VenueException(VenueFailure.Unavailable, $"No adapter is installed for {connection.Venue}.");
            if (!vault.IsAvailable)
            {
                throw new VenueException(VenueFailure.Unavailable, "The master key is not configured, so stored keys cannot be read.");
            }

            VenueCredentials credentials = vault.Unprotect(connection.EncryptedCredentials);
            VenueSnapshot snapshot = await adapter.GetSnapshotAsync(credentials, cancellationToken);
            if (await SwitchedOffMeanwhileAsync(connection, cancellationToken))
            {
                return false;
            }

            // A reading that leaves out part of the account, straight after one that had all of it, is most likely the
            // venue stumbling (busy, a late timestamp), not the futures account ceasing to exist. Taking it would drop
            // that part from the book and bring it back a minute later. So it is treated as a failed reading, and only
            // accepted once it has stayed that way for a few readings.
            if (snapshot.Notes is { Count: > 0 } && connection.ConsecutiveFailures < 4 && connection.LastSnapshotJson is { } before
                && JsonSerializer.Deserialize<VenueSnapshot>(before, ManagerBookWriter.Json) is { Notes: null or { Count: 0 } })
            {
                throw new VenueException(VenueFailure.Unavailable, $"Part of the account could not be read this time. {snapshot.Notes[0]} The last complete reading is kept.");
            }

            // A connection whose value was not in the book until now brings it in as money arriving, not as a gain.
            if (connection.LastSnapshotJson is null)
            {
                connection.BookedAt = now;
                AddBookChange(connection.ManagerAccountId, ManagerBookWriter.NavOf(snapshot), now, $"{adapter.DisplayName} connection \"{connection.Label}\" added to the book");
            }

            connection.LastSnapshotJson = JsonSerializer.Serialize(snapshot, ManagerBookWriter.Json);
            connection.State = ConnectionState.Live;
            connection.LastSuccessAt = now;
            connection.LastError = null;
            connection.ConsecutiveFailures = 0;
            await database.SaveChangesAsync(cancellationToken);
            await ReadTransfersAsync(connection, adapter, credentials, snapshot, previousSuccess, now, cancellationToken);
            // The book just changed: rewrite today, sample it, check the fund's rules, tell whoever is watching.
            await books.RefreshAsync(connection.ManagerAccountId, cancellationToken, paced);
        }
        catch (VenueException e)
        {
            // The book is left as it was: a failed read is an error, never an empty book.
            if (await SwitchedOffMeanwhileAsync(connection, cancellationToken))
            {
                return false;
            }

            connection.State = ConnectionState.Failed;
            connection.LastError = e.Message.Length <= 500 ? e.Message : e.Message[..500];
            connection.ConsecutiveFailures++;
            await database.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Connection {ConnectionId} on {Venue} failed: {Reason}", connection.Id, connection.Venue, e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Anything else (a key that cannot be decrypted, an answer in a shape nobody expected, the book refusing
            // to save) must show on the connection too: left silent it would keep its "Live" badge over stale data
            // and be tried again every minute. The note is written by a statement of its own, because whatever
            // failed may have left this unit of work unable to save.
            logger.LogError(e, "Connection {ConnectionId} on {Venue} failed unexpectedly.", connection.Id, connection.Venue);
            string reason = $"Unexpected error: {e.GetType().Name}. The server log has the details.";
            database.DiscardChanges();
            VenueConnection? stored = await database.VenueConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
            if (stored is not null && stored.State != ConnectionState.Disabled)
            {
                stored.State = ConnectionState.Failed;
                stored.LastError = reason;
                stored.LastAttemptAt = now;
                stored.ConsecutiveFailures++;
                await database.SaveChangesAsync(cancellationToken);
            }
        }

        return true;
    }

    /// <summary>
    /// True when an administrator switched the connection off while the venue was being read. The read is dropped:
    /// saving it would switch the connection back on and put its positions back in the book.
    /// </summary>
    private async Task<bool> SwitchedOffMeanwhileAsync(VenueConnection connection, CancellationToken cancellationToken)
    {
        ConnectionState? stored = await database.VenueConnections.AsNoTracking().Where(c => c.Id == connection.Id).Select(c => (ConnectionState?)c.State).FirstOrDefaultAsync(cancellationToken);
        return stored is null or ConnectionState.Disabled;
    }

    /// <summary>Value entering or leaving a manager's book because a connection did, kept as a cash flow so it is not read as performance.</summary>
    private void AddBookChange(int managerAccountId, decimal usd, DateTimeOffset now, string note) => database.CashFlows.Add(BookChange(managerAccountId, usd, now, note));

    public static CashFlow BookChange(int managerAccountId, decimal usd, DateTimeOffset now, string note) =>
        new()
        {
            ManagerAccountId = managerAccountId,
            Date = DateOnly.FromDateTime(now.UtcDateTime),
            Kind = usd >= 0m ? CashFlowKind.Subscription : CashFlowKind.Redemption,
            AmountUsd = Math.Round(usd, 2),
            Note = note.Length <= 200 ? note : note[..200],
            IsBookChange = true,
        };

    /// <summary>
    /// Records deposits and withdrawals as cash flows, so returns are measured on the money that was there. Trouble
    /// here is noted on the connection and tried again; it never holds back the positions.
    /// </summary>
    private async Task ReadTransfersAsync(VenueConnection connection, IVenueAdapter adapter, VenueCredentials credentials, VenueSnapshot snapshot, DateTimeOffset? previousSuccess, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (connection.TransfersCheckedAt is DateTimeOffset last && now - last < TransferInterval && connection.TransfersError is null)
        {
            return;
        }

        try
        {
            // Before the first read there is no NAV for a transfer to distort, so history starts with the connection.
            DateTimeOffset since = (connection.TransfersCheckedAt ?? connection.CreatedAt) - TransferOverlap;
            IReadOnlyList<VenueTransfer> transfers = await adapter.GetTransfersAsync(credentials, since, cancellationToken);
            string prefix = $"{connection.Venue}:{connection.Id}:";
            List<string> ids = transfers.Select(t => prefix + t.Id).ToList();
            HashSet<string> known = (await database.CashFlows.Where(f => f.ManagerAccountId == connection.ManagerAccountId && ids.Contains(f.ExternalId!)).Select(f => f.ExternalId!).ToListAsync(cancellationToken)).ToHashSet();
            DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
            // Only what moved after the connection's value entered the book: anything earlier is already part of that value.
            DateTimeOffset inBookSince = connection.BookedAt ?? connection.CreatedAt;
            foreach (VenueTransfer transfer in transfers.Where(t => t.Quantity != 0m && t.At >= inBookSince && known.Add(prefix + t.Id)))
            {
                decimal usd = Math.Round(transfer.Quantity * await UsdPriceAsync(transfer.Asset.ToUpperInvariant(), snapshot, cancellationToken), 2);
                database.CashFlows.Add(
                    new CashFlow
                    {
                        ManagerAccountId = connection.ManagerAccountId,
                        // The day whose NAV first shows the money: the transfer's own day while reads were running,
                        // otherwise today, when this read is the first to see it.
                        Date = previousSuccess is DateTimeOffset seen && seen >= transfer.At ? DateOnly.FromDateTime(transfer.At.UtcDateTime) : today,
                        Kind = usd >= 0m ? CashFlowKind.Subscription : CashFlowKind.Redemption,
                        AmountUsd = usd,
                        Note = $"{adapter.DisplayName} {(usd >= 0m ? "deposit" : "withdrawal")} of {Math.Abs(transfer.Quantity):0.########} {transfer.Asset.ToUpperInvariant()}",
                        ExternalId = prefix + transfer.Id,
                    }
                );
            }

            connection.TransfersCheckedAt = now;
            connection.TransfersError = null;
        }
        catch (VenueException e)
        {
            connection.TransfersError = e.Message.Length <= 500 ? e.Message : e.Message[..500];
            logger.LogWarning("Transfers of connection {ConnectionId} on {Venue} could not be read: {Reason}", connection.Id, connection.Venue, e.Message);
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>USD per unit: 1 for cash, else the price the account itself is valued at, else the latest daily close.</summary>
    private async Task<decimal> UsdPriceAsync(string asset, VenueSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Positions.FirstOrDefault(p => p.Type is InstrumentType.Cash or InstrumentType.Spot or InstrumentType.Perp && p.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)) is { } held
            && held.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase))
        {
            return held.MarkPrice;
        }

        // A dollar coin that has left the account entirely is still a dollar; no price history is kept for cash.
        if (DollarCoins.Contains(asset) || await database.Assets.AnyAsync(a => a.Symbol == asset && a.AssetClass == AssetClass.Cash, cancellationToken))
        {
            return 1m;
        }

        decimal? close = await database.PriceBars.Where(b => b.Asset.Symbol == asset).OrderByDescending(b => b.Date).Select(b => (decimal?)b.Close).FirstOrDefaultAsync(cancellationToken);
        return close ?? throw new VenueException(VenueFailure.Unavailable, $"a transfer of {asset} cannot be valued: the account no longer holds it and there is no price history for it.");
    }
}

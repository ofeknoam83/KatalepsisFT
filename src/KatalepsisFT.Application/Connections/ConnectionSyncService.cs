using System.Text.Json;
using KatalepsisFT.Application.Alerts;
using KatalepsisFT.Domain.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KatalepsisFT.Application.Connections;

/// <summary>Reads one connection from its venue and, on a complete answer, rewrites the manager's book for today.</summary>
public sealed class ConnectionSyncService(IFtDbContext database, VenueAdapterRegistry adapters, ICredentialVault vault, ManagerBookWriter writer, AlertEvaluator alerts, TimeProvider clock, ILogger<ConnectionSyncService> logger)
{
    /// <summary>Longest wait between attempts on a connection that keeps failing.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    /// <summary>When a failing connection may be tried again: 1, 2, 4 … minutes, capped.</summary>
    public static TimeSpan Backoff(int consecutiveFailures) =>
        consecutiveFailures <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(Math.Min(MaxBackoff.TotalMinutes, Math.Pow(2, Math.Min(consecutiveFailures, 10) - 1)));

    /// <returns>False when the connection does not exist or is switched off.</returns>
    public async Task<bool> SyncAsync(int connectionId, CancellationToken cancellationToken)
    {
        VenueConnection? connection = await database.VenueConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null || connection.State == ConnectionState.Disabled)
        {
            return false;
        }

        DateTimeOffset now = clock.GetUtcNow();
        connection.LastAttemptAt = now;
        try
        {
            IVenueAdapter adapter = adapters.Find(connection.Venue) ?? throw new VenueException(VenueFailure.Unavailable, $"No adapter is installed for {connection.Venue}.");
            if (!vault.IsAvailable)
            {
                throw new VenueException(VenueFailure.Unavailable, "The master key is not configured, so stored keys cannot be read.");
            }

            VenueSnapshot snapshot = await adapter.GetSnapshotAsync(vault.Unprotect(connection.EncryptedCredentials), cancellationToken);
            connection.LastSnapshotJson = JsonSerializer.Serialize(snapshot, ManagerBookWriter.Json);
            connection.State = ConnectionState.Live;
            connection.LastSuccessAt = now;
            connection.LastError = null;
            connection.ConsecutiveFailures = 0;
            await database.SaveChangesAsync(cancellationToken);
            await writer.WriteAsync(connection.ManagerAccountId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);
            // The book just changed, so the fund's rules are checked against it straight away.
            int fundId = await database.ManagerAccounts.Where(m => m.Id == connection.ManagerAccountId).Select(m => m.Sleeve.FundId).SingleAsync(cancellationToken);
            await alerts.EvaluateAsync(fundId, cancellationToken);
        }
        catch (VenueException e)
        {
            // The book is left as it was: a failed read is an error, never an empty book.
            connection.State = ConnectionState.Failed;
            connection.LastError = e.Message.Length <= 500 ? e.Message : e.Message[..500];
            connection.ConsecutiveFailures++;
            await database.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Connection {ConnectionId} on {Venue} failed: {Reason}", connection.Id, connection.Venue, e.Message);
        }

        return true;
    }
}

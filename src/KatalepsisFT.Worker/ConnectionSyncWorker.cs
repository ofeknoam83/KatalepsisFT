using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Infrastructure.Health;
using KatalepsisFT.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Worker;

/// <summary>
/// Reads every connection of this worker's venues in turn, on an interval. A connection that keeps failing is tried
/// less and less often, up to <see cref="ConnectionSyncService.MaxBackoff"/>. One connection failing never stops the others.
/// </summary>
public sealed class ConnectionSyncWorker(IServiceScopeFactory scopes, WorkerIdentity identity, TimeProvider clock, ILogger<ConnectionSyncWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>Pause between two accounts, so a venue is not hit by all of them at once.</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The database being away for a moment must not end the worker.
                logger.LogError(e, "A round of reading connections failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        List<int> due;
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            Venue[] venues = scope.ServiceProvider.GetRequiredService<VenueAdapterRegistry>().All.Where(a => a.WorkerGroup == identity.Group).Select(a => a.Venue).ToArray();
            DateTimeOffset now = clock.GetUtcNow();
            var candidates = await scope
                .ServiceProvider.GetRequiredService<FtDbContext>()
                .VenueConnections.AsNoTracking()
                .Where(c => venues.Contains(c.Venue) && c.State != ConnectionState.Disabled)
                .Select(c => new { c.Id, c.LastAttemptAt, c.ConsecutiveFailures })
                .ToListAsync(cancellationToken);
            due = candidates.Where(c => c.LastAttemptAt is null || c.LastAttemptAt.Value + ConnectionSyncService.Backoff(c.ConsecutiveFailures) <= now).Select(c => c.Id).ToList();
        }

        foreach (int id in due)
        {
            // A scope per connection: one failed write cannot leave half-saved state for the next.
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<ConnectionSyncService>().SyncAsync(id, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Reading connection {ConnectionId} failed unexpectedly.", id);
            }

            await Task.Delay(Stagger, clock, cancellationToken);
        }
    }
}

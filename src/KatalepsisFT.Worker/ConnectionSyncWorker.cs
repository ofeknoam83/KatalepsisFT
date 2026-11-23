using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Infrastructure.Health;
using KatalepsisFT.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Worker;

/// <summary>
/// Reads the connections of this worker's venues every couple of seconds, each no more often than its venue allows.
/// Managers are read side by side; the connections of one manager one after another, since they write the same book.
/// A connection that keeps failing is tried less and less often, up to <see cref="ConnectionSyncService.MaxBackoff"/>.
/// One connection failing never stops the others.
/// </summary>
public sealed class ConnectionSyncWorker(IServiceScopeFactory scopes, WorkerIdentity identity, TimeProvider clock, ILogger<ConnectionSyncWorker> logger) : BackgroundService
{
    /// <summary>How often the worker looks for connections that are due. Each read is pushed to open browsers as soon as it is stored.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>Accounts of one venue that may be read at the venue's full pace; with more, each is read proportionally less often, because venues also count requests per caller address.</summary>
    public const int AccountsAtFullPace = 3;

    /// <summary>Managers read at the same time.</summary>
    private const int Parallelism = 8;

    /// <summary>A timer that fires a moment early must not make a read wait a whole further round.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(300);

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
        List<List<int>> dueByManager;
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            Dictionary<Venue, TimeSpan> pace = scope.ServiceProvider.GetRequiredService<VenueAdapterRegistry>().All.Where(a => a.WorkerGroup == identity.Group).ToDictionary(a => a.Venue, a => a.MinReadInterval);
            Venue[] venues = pace.Keys.ToArray();
            DateTimeOffset now = clock.GetUtcNow();
            var candidates = await scope
                .ServiceProvider.GetRequiredService<FtDbContext>()
                .VenueConnections.AsNoTracking()
                .Where(c => venues.Contains(c.Venue) && c.State != ConnectionState.Disabled)
                .Select(c => new { c.Id, c.ManagerAccountId, c.Venue, c.LastAttemptAt, c.ConsecutiveFailures })
                .ToListAsync(cancellationToken);
            Dictionary<Venue, int> accounts = candidates.GroupBy(c => c.Venue).ToDictionary(g => g.Key, g => g.Count());
            dueByManager = candidates
                .Where(c =>
                {
                    TimeSpan wait = pace[c.Venue] * Math.Ceiling(accounts[c.Venue] / (double)AccountsAtFullPace);
                    TimeSpan backoff = ConnectionSyncService.Backoff(c.ConsecutiveFailures);
                    return c.LastAttemptAt is null || c.LastAttemptAt.Value + (backoff > wait ? backoff : wait) - Tolerance <= now;
                })
                .GroupBy(c => c.ManagerAccountId)
                .Select(g => g.Select(c => c.Id).ToList())
                .ToList();
        }

        await Parallel.ForEachAsync(
            dueByManager,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (connectionIds, token) =>
            {
                foreach (int id in connectionIds)
                {
                    // A scope per connection: one failed write cannot leave half-saved state for the next.
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<ConnectionSyncService>().SyncAsync(id, token, paced: true);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogError(e, "Reading connection {ConnectionId} failed unexpectedly.", id);
                    }
                }
            }
        );
    }
}

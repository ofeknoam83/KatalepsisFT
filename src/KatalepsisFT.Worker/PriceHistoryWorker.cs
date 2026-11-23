using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Prices;

namespace KatalepsisFT.Worker;

/// <summary>
/// Keeps the daily price history of every asset up to date, and loads it for an asset soon after a connection first
/// brings it in. Risk figures need that history: without it an asset's beta is assumed and its traded volume unknown.
/// The daily job does the same once a day; this closes the gap in between. It also revalues the books that consist
/// of hand-entered positions only, since nothing else would.
/// </summary>
public sealed class PriceHistoryWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PriceHistoryWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>How long an asset the price source does not list is left alone before it is asked about again.</summary>
    public static readonly TimeSpan RetryUnknownAfter = TimeSpan.FromHours(24);

    /// <summary>History loaded for an asset seen for the first time: enough for a year of risk figures.</summary>
    private const int HistoryDays = 400;

    private readonly Dictionary<string, DateTimeOffset> _notListed = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, clock);
        do
        {
            try
            {
                DateTimeOffset now = clock.GetUtcNow();
                foreach (string stale in _notListed.Where(e => now - e.Value > RetryUnknownAfter).Select(e => e.Key).ToList())
                {
                    _notListed.Remove(stale);
                }

                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                PriceBackfillResult result = await scope
                    .ServiceProvider.GetRequiredService<PriceBackfillService>()
                    .RunAsync(DateOnly.FromDateTime(now.UtcDateTime).AddDays(-HistoryDays), stoppingToken, _notListed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
                foreach (string symbol in result.AssetsWithoutData)
                {
                    _notListed[symbol] = now;
                }

                // Books made only of hand-entered positions have no venue read to trigger them: revalue them here.
                await scope.ServiceProvider.GetRequiredService<ManagerBookService>().RefreshManualOnlyAsync(stoppingToken);

                if (result.BarsAdded > 0 || result.AssetsWithoutData.Count > 0)
                {
                    logger.LogInformation("Price history: {Bars} daily bars added; no data for {Missing} asset(s).", result.BarsAdded, result.AssetsWithoutData.Count);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The price source or the database being away for a moment must not end the worker.
                logger.LogWarning(e, "A round of loading price history failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

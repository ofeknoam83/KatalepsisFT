using KatalepsisFT.Application.Demo;

namespace KatalepsisFT.Worker;

/// <summary>Values the demo fund at live prices every couple of seconds, so it moves the way a connected fund does.</summary>
public sealed class DemoLiveWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DemoLiveWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>Intraday samples and alert rules are heavier and do not need the same pace.</summary>
    public static readonly TimeSpan FullInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, clock);
        DateTimeOffset lastFull = DateTimeOffset.MinValue;
        int failures = 0;
        do
        {
            try
            {
                DateTimeOffset now = clock.GetUtcNow();
                bool full = now - lastFull >= FullInterval;
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DemoLiveService>().RevalueAsync(full, stoppingToken);
                if (full)
                {
                    lastFull = now;
                }

                failures = 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The price source or the database being away must not end the worker. One line a minute, not one every two seconds.
                if (failures++ % 30 == 0)
                {
                    logger.LogWarning(e, "The demo fund could not be revalued.");
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

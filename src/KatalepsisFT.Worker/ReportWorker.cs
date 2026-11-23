using KatalepsisFT.Application.Reports;

namespace KatalepsisFT.Worker;

/// <summary>Sends the reports that are due. Looks once a minute; a report goes out at most once a day.</summary>
public sealed class ReportWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReportWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, clock);
        do
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                int sent = await scope.ServiceProvider.GetRequiredService<ReportScheduleService>().RunDueAsync(stoppingToken);
                if (sent > 0)
                {
                    logger.LogInformation("{Count} scheduled report(s) sent.", sent);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The database being away for a moment must not end the worker.
                logger.LogWarning(e, "A round of sending reports failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

using KatalepsisFT.Application.Connections;

namespace KatalepsisFT.Worker;

/// <summary>
/// Rewrites the book of managers whose trading accounts count in it and who have no venue read to trigger the
/// rewrite. This is how a fill reaches holdings, exposure and NAV, and how an account that stops being counted
/// leaves the book.
/// </summary>
public sealed class TradingBookWorker(IServiceScopeFactory scopes, ILogger<TradingBookWorker> logger) : BackgroundService
{
    /// <summary>A fill is in the book within this long.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<int> managerIds;
                await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
                {
                    managerIds = await scope.ServiceProvider.GetRequiredService<ManagerBookService>().TradedOnlyAsync(stoppingToken);
                }

                foreach (int id in managerIds)
                {
                    try
                    {
                        // Each book on its own, so one that cannot be written does not hold back the others.
                        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<ManagerBookService>().RefreshAsync(id, stoppingToken, paced: true);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogWarning(e, "The book of manager {ManagerId} could not be rewritten from its trading accounts.", id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning("Trading accounts could not be brought into the books this round: {Reason}", e.Message);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

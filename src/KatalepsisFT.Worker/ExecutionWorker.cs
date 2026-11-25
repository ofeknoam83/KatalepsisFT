using System.Diagnostics;
using KatalepsisFT.Application;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Infrastructure.Execution;
using Npgsql;
using StackExchange.Redis;

namespace KatalepsisFT.Worker;

/// <summary>
/// Which assets the execution service always follows, how many more books it may open because somebody asked, and
/// the key that opens the trading-key vault.
/// </summary>
public sealed record ExecutionOptions(IReadOnlyList<string> Assets, string? MasterKey, int MostBooks = BookDemand.DefaultMost)
{
    /// <summary>How long a book stays open after it was last read.</summary>
    public TimeSpan BooksKeptFor { get; init; } = KeyValueBookFeed.WantedFor;

    public static readonly IReadOnlyList<string> DefaultAssets = ["BTC", "ETH", "SOL", "XRP", "DOGE", "BNB", "LINK", "AVAX", "ADA", "SUI"];

    public static ExecutionOptions From(IConfiguration configuration)
    {
        List<string> assets = (configuration["EXEC_ASSETS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Length <= 12 && a.All(char.IsLetterOrDigit))
            .Select(a => a.ToUpperInvariant())
            .Distinct()
            .ToList();
        int most = int.TryParse(configuration["EXEC_MAX_BOOKS"], out int given) && given is >= 0 and <= 500 ? given : BookDemand.DefaultMost;
        TimeSpan kept = int.TryParse(configuration["EXEC_BOOK_IDLE_SECONDS"], out int seconds) && seconds is >= 10 and <= 3600 ? TimeSpan.FromSeconds(seconds) : KeyValueBookFeed.WantedFor;
        return new ExecutionOptions(assets.Count > 0 ? assets : DefaultAssets, configuration["EXEC_MASTER_KEY"], most) { BooksKeptFor = kept };
    }
}

/// <summary>
/// Keeps the venues' market data streams open, times a request to each venue every few seconds, and publishes
/// the consolidated book and the service's own state for the web service to show.
/// </summary>
public sealed class MarketDataWorker(
    IEnumerable<BookStream> streams,
    IEnumerable<BookPoller> pollers,
    ConsolidatedBook books,
    VenueStatusBoard board,
    IConnectionMultiplexer keyValue,
    IHttpClientFactory clients,
    ExecutionOptions options,
    BookDemand demand,
    TimeProvider clock,
    ILogger<MarketDataWorker> logger
) : BackgroundService
{
    private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TimeEvery = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<BookStream> all = streams.ToList();
        logger.LogInformation("Always following {Assets}, and up to {Most} more instruments on demand, on {Venues}.", string.Join(", ", options.Assets), options.MostBooks, string.Join(", ", all.Select(s => $"{s.Venue} {s.Type}")));
        List<Task> running = all.Select(s => new BookStreamRunner(s, books, board, clients.CreateClient("exec"), clock, logger).RunAsync(() => demand.For(s.Venue, s.Type), stoppingToken)).ToList();
        running.AddRange(all.Select(s => TimeAsync(s, stoppingToken)));
        running.AddRange(pollers.Select(p => p.RunAsync(() => demand.For(p.Venue, p.Type), books, board, clients.CreateClient("exec"), clock, logger, stoppingToken)));
        running.Add(PublishAsync(all.Select(s => s.Venue).Distinct().ToList(), stoppingToken));
        await Task.WhenAll(running);
    }

    /// <summary>
    /// Times a small request to the venue on a connection that is kept open, which is what sending an order costs
    /// in travel: there and back, without the venue's own matching time.
    /// </summary>
    private async Task TimeAsync(BookStream stream, CancellationToken stopping)
    {
        HttpClient http = clients.CreateClient("exec");
        bool warm = false;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using HttpRequestMessage request = stream.TimingRequest();
                long started = Stopwatch.GetTimestamp();
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, stopping);
                double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                // The first request also pays for opening the connection, which an order would not.
                if (warm)
                {
                    board.RoundTrip(stream.Venue, milliseconds);
                }

                warm = true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !stopping.IsCancellationRequested)
            {
                warm = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeEvery, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PublishAsync(IReadOnlyList<Venue> venues, CancellationToken stopping)
    {
        KeyValueBookFeed feed = new(keyValue);
        DateTimeOffset warned = DateTimeOffset.MinValue;
        // The first summary a minute in, once every venue has been timed a few times; then every ten minutes.
        DateTimeOffset summarise = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                ExecutionStatus status = board.Snapshot(books, venues);
                await feed.PublishAsync(books, status);
                if (status.At >= summarise)
                {
                    summarise = status.At + TimeSpan.FromMinutes(10);
                    logger.LogInformation(
                        "Market data: {Venues}. Last look at open orders took {Round} ms.",
                        string.Join("; ", status.Venues.Select(v => $"{v.Venue} {(v.Connected ? v.Books + " live books" : "no data")}, round trip {(v.RoundTripMs is double ms ? ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms" : "not measured")}")),
                        status.RoundMs
                    );
                }
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                if (clock.GetUtcNow() - warned > TimeSpan.FromMinutes(1))
                {
                    warned = clock.GetUtcNow();
                    logger.LogWarning("The consolidated book could not be published: {Reason}", e.Message);
                }
            }

            try
            {
                await Task.Delay(PublishEvery, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>
/// Runs the order engine. Only one copy of the execution service may act on orders at a time: the one holding a lock
/// in Postgres that lives exactly as long as its database connection. A second copy waits as a standby and takes
/// over within seconds if the first one stops. Everything the engine needs to carry on is in the order log, so a
/// restart picks up every open order where it was.
/// </summary>
public sealed class OrderEngineWorker(
    IServiceScopeFactory scopes,
    NpgsqlDataSource dataSource,
    IConnectionMultiplexer keyValue,
    VenueStatusBoard board,
    ExecutionOptions options,
    OpenedTradingKeys keys,
    ILogger<OrderEngineWorker> logger
) : BackgroundService
{
    /// <summary>The number of the Postgres advisory lock that makes one execution service the acting one.</summary>
    private const long LeaderLock = 7302;
    private static readonly TimeSpan Round = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StandbyWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CheckLockEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VaultRetry = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A new order wakes the engine at once instead of waiting for its next round.
        using SemaphoreSlim wake = new(0, 1);
        try
        {
            await keyValue.GetSubscriber().SubscribeAsync(RedisChannel.Literal(KeyValueExecutionSignal.Channel), (_, _) =>
            {
                try
                {
                    wake.Release();
                }
                catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException)
                {
                    // Already woken, or stopping.
                }
            });
        }
        catch (RedisException e)
        {
            logger.LogWarning("The engine cannot be woken by new orders and will find them on its rounds: {Reason}", e.Message);
        }

        bool waitingLogged = false;
        // On its own, so that leading on orders never waits for it and it keeps trying while orders are handled.
        Task vault = PrepareVaultUntilSettledAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using NpgsqlConnection held = await dataSource.OpenConnectionAsync(stoppingToken);
                if (!await AskAsync(held, $"SELECT pg_try_advisory_lock({LeaderLock})", stoppingToken))
                {
                    if (!waitingLogged)
                    {
                        waitingLogged = true;
                        logger.LogInformation("Another execution service is acting on orders; this one is standing by.");
                    }

                    await held.CloseAsync();
                    await Task.Delay(StandbyWait, stoppingToken);
                    continue;
                }

                waitingLogged = false;
                board.IsLeader = true;
                try
                {
                    await LeadAsync(held, wake, stoppingToken);
                }
                finally
                {
                    board.IsLeader = false;
                    // Closing a pooled connection only hands it back; the lock would stay with it. Give the lock up
                    // by name. If the connection itself is broken, Postgres has already dropped the lock.
                    try
                    {
                        await AskAsync(held, $"SELECT pg_advisory_unlock({LeaderLock})", CancellationToken.None);
                    }
                    catch (Exception e) when (e is NpgsqlException or InvalidOperationException or ObjectDisposedException)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "The order engine stopped and will start again.");
                try
                {
                    await Task.Delay(StandbyWait, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        await vault;
    }

    private async Task LeadAsync(NpgsqlConnection held, SemaphoreSlim wake, CancellationToken stopping)
    {
        using (IServiceScope first = scopes.CreateScope())
        {
            IFtDbContext database = first.ServiceProvider.GetRequiredService<IFtDbContext>();
            int open = database.Orders.Count(o => (int)o.Status < (int)KatalepsisFT.Domain.Execution.OrderStatus.Filled);
            logger.LogInformation("This execution service is now acting on orders. {Open} open orders were found in the order log.", open);
        }

        long lockChecked = Stopwatch.GetTimestamp();
        while (!stopping.IsCancellationRequested)
        {
            // The lock lives with the connection. If the connection is gone, another copy may already be acting.
            if (Stopwatch.GetElapsedTime(lockChecked) >= CheckLockEvery)
            {
                if (!await AskAsync(held, "SELECT true", stopping))
                {
                    return;
                }

                lockChecked = Stopwatch.GetTimestamp();
            }

            long started = Stopwatch.GetTimestamp();
            using (IServiceScope scope = scopes.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ExecutionEngine>().RunOnceAsync(stopping);
            }

            board.RoundMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
            await wake.WaitAsync(Round, stopping);
        }
    }

    /// <summary>
    /// Makes sure the trading-key vault has its pair of keys. Without EXEC_MASTER_KEY the vault stays shut: paper
    /// trading works, and adding a trading key is refused with a clear message.
    /// </summary>
    /// <summary>
    /// The database may not be ready when this service starts (a release brings its tables a moment later), so the
    /// vault is tried again until it has an answer either way.
    /// </summary>
    private async Task PrepareVaultUntilSettledAsync(CancellationToken stopping)
    {
        try
        {
            while (!await PrepareVaultAsync(stopping))
            {
                await Task.Delay(VaultRetry, stopping);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <returns>False when it could not be found out, and is worth trying again.</returns>
    private async Task<bool> PrepareVaultAsync(CancellationToken stopping)
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            SealedTradingVault.TradingKeyOpener? opener = await SealedTradingVault.EnsureKeysAsync(scope.ServiceProvider.GetRequiredService<IFtDbContext>(), options.MasterKey, stopping);
            if (opener is null)
            {
                logger.LogWarning("EXEC_MASTER_KEY is not set (or is shorter than 32 characters). Trading keys cannot be stored until it is.");
            }
            else
            {
                keys.Use(opener);
                logger.LogInformation("The trading-key vault is ready.");
            }

            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("The trading-key vault could not be prepared yet and will be tried again: {Reason}", e.Message);
            return false;
        }
    }

    private static async Task<bool> AskAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 5 };
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }
}

/// <summary>The key that opens stored trading keys, once the vault has been prepared. It never leaves this service.</summary>
public sealed class OpenedTradingKeys : ITradingKeys
{
    private volatile SealedTradingVault.TradingKeyOpener? _opener;

    public bool Ready => _opener is not null;

    public void Use(SealedTradingVault.TradingKeyOpener opener) => _opener = opener;

    public KatalepsisFT.Application.Connections.VenueCredentials Open(string sealedKey) =>
        (_opener ?? throw new InvalidOperationException("The trading-key vault has not been prepared.")).Open(sealedKey);
}

/// <summary>
/// Works the orders of real accounts and keeps reading what those accounts hold at their venues. It only acts while
/// this copy of the execution service is the acting one. Accounts are worked side by side, so a slow answer from a
/// venue for one account does not hold up another's orders; within an account, one thing happens at a time.
/// </summary>
public sealed class LiveOrdersWorker(IServiceScopeFactory scopes, IConnectionMultiplexer keyValue, VenueStatusBoard board, TimeProvider clock, ILogger<LiveOrdersWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Round = TimeSpan.FromMilliseconds(100);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task> _working = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTimeOffset> _read = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using SemaphoreSlim wake = new(0, 1);
        try
        {
            await keyValue.GetSubscriber().SubscribeAsync(RedisChannel.Literal(KeyValueExecutionSignal.Channel), (_, _) =>
            {
                try
                {
                    wake.Release();
                }
                catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException)
                {
                }
            });
        }
        catch (RedisException e)
        {
            logger.LogWarning("Real orders cannot be woken by new orders and will be found on the rounds: {Reason}", e.Message);
        }

        bool announced = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (board.IsLeader)
                {
                    LiveWork work;
                    using (IServiceScope scope = scopes.CreateScope())
                    {
                        LiveExecution live = scope.ServiceProvider.GetRequiredService<LiveExecution>();
                        if (!announced)
                        {
                            announced = true;
                            logger.LogInformation("Real orders can be sent to: {Venues}.", string.Join(", ", live.Venues));
                        }

                        work = await live.WorkAsync(stoppingToken);
                    }

                    DateTimeOffset now = clock.GetUtcNow();
                    foreach (int accountId in work.Accounts.Union(work.OpenOrders.Keys))
                    {
                        List<long> orders = work.OpenOrders.GetValueOrDefault(accountId) ?? [];
                        bool readDue = !_read.TryGetValue(accountId, out DateTimeOffset at) || now - at >= LiveExecution.ReadAccountEvery;
                        if ((orders.Count > 0 || readDue) && !_working.ContainsKey(accountId))
                        {
                            _working[accountId] = Task.Run(() => WorkAccountAsync(accountId, orders, readDue, stoppingToken), stoppingToken);
                        }
                    }
                }

                await wake.WaitAsync(Round, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "The round for real orders failed and will run again.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // What is under way is allowed to finish: an order half way to a venue must get its answer written down.
        await Task.WhenAll(_working.Values.ToArray()).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task WorkAccountAsync(int accountId, List<long> orders, bool readDue, CancellationToken stopping)
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            LiveExecution live = scope.ServiceProvider.GetRequiredService<LiveExecution>();
            bool changed = false;
            foreach (long orderId in orders)
            {
                try
                {
                    // Not cancelled half way: once an order is on its way out, its answer must be recorded.
                    changed |= await live.HandleAsync(orderId, CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Real order {OrderId} could not be handled; it is tried again.", orderId);
                }
            }

            if ((changed || readDue) && !stopping.IsCancellationRequested)
            {
                _read[accountId] = clock.GetUtcNow();
                await live.ReadAccountAsync(accountId, CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Real account {AccountId} could not be worked; it is tried again.", accountId);
        }
        finally
        {
            _working.TryRemove(accountId, out _);
        }
    }
}

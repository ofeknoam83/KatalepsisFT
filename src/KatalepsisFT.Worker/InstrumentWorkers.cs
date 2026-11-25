using KatalepsisFT.Application;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace KatalepsisFT.Worker;

/// <summary>
/// Reads what every venue lists, once an hour, and keeps it in the database for the search on the trading page and in
/// memory for the market data streams. A venue that cannot be read keeps its last list; nothing is forgotten because
/// one answer failed.
/// </summary>
public sealed class InstrumentListWorker(IServiceScopeFactory scopes, IHttpClientFactory clients, BookDemand demand, TimeProvider clock, ILogger<InstrumentListWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await UseStoredAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            bool complete = false;
            try
            {
                complete = await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning("The venues' instrument lists could not be refreshed and will be tried again: {Reason}", e.Message);
            }

            try
            {
                await Task.Delay(complete ? Every : AfterFailure, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>What was read before this service last stopped, so that books can be opened straight away.</summary>
    private async Task UseStoredAsync(CancellationToken stopping)
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            IFtDbContext database = scope.ServiceProvider.GetRequiredService<IFtDbContext>();
            // Listed when last read, open for orders or not. What a venue had stopped listing is not asked for.
            var newest = await database.ListedInstruments.AsNoTracking().GroupBy(i => new { i.Venue, i.Type, i.Quote }).Select(g => new { g.Key.Venue, g.Key.Type, g.Key.Quote, At = g.Max(i => i.SeenAt) }).ToListAsync(stopping);
            var every = await database.ListedInstruments.AsNoTracking().Select(i => new { i.Venue, i.Type, i.Asset, i.Quote, i.SeenAt }).ToListAsync(stopping);
            var stored = every.Where(i => newest.Any(n => n.Venue == i.Venue && n.Type == i.Type && n.Quote == i.Quote && n.At == i.SeenAt)).ToList();
            foreach (var group in stored.GroupBy(i => (i.Venue, i.Type, i.Quote)))
            {
                demand.Listed(group.Key.Venue, group.Key.Type, group.Select(i => i.Asset), group.Key.Quote);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The table may not be there yet on the first start after a release; the refresh that follows fills it.
            logger.LogInformation("No stored instrument lists were read: {Reason}", e.Message);
        }
    }

    /// <summary>A step as it can be stored: fourteen decimal places, and nothing absurd.</summary>
    private static decimal? Kept(decimal? step) => step is > 0m and < 1_000_000_000m ? Math.Round(step.Value, 14) : null;

    /// <returns>True when every venue answered.</returns>
    private async Task<bool> RefreshAsync(CancellationToken stopping)
    {
        HttpClient http = clients.CreateClient("lists");
        var answers = await Task.WhenAll(InstrumentListings.All.Select(async listing =>
        {
            try
            {
                List<string> pages = [];
                HttpRequestMessage? request = listing.Request();
                // A list that comes in pages is followed to its end, within reason.
                for (int page = 0; request is not null && page < 20; page++)
                {
                    using HttpRequestMessage sending = request;
                    using HttpResponseMessage response = await http.SendAsync(sending, stopping);
                    response.EnsureSuccessStatusCode();
                    string answer = await response.Content.ReadAsStringAsync(stopping);
                    pages.Add(answer);
                    request = listing.After(answer);
                }

                IReadOnlyList<ListedSymbol> symbols = listing.ReadAnswers(pages);
                // An empty list from a venue that had instruments an hour ago is a bad answer, not a venue that closed.
                return (Listing: listing, Symbols: symbols.Count > 0 ? symbols : null, Reason: symbols.Count > 0 ? null : "the list was empty");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException && !stopping.IsCancellationRequested)
            {
                return (Listing: listing, Symbols: (IReadOnlyList<ListedSymbol>?)null, Reason: e.Message);
            }
        }));

        using IServiceScope scope = scopes.CreateScope();
        IFtDbContext database = scope.ServiceProvider.GetRequiredService<IFtDbContext>();
        DateTimeOffset now = clock.GetUtcNow();
        List<string> failed = [];
        List<string> read = [];
        int total = 0;
        foreach (var (listing, symbols, reason) in answers)
        {
            if (symbols is null)
            {
                failed.Add($"{listing.Venue} {listing.Type} ({reason})");
                continue;
            }

            total += symbols.Count;
            read.Add($"{listing.Venue} {listing.Type}{(listing.Quote == "USDT" ? string.Empty : " " + listing.Quote)} {symbols.Count(s => s.Tradable)} of {symbols.Count} open");
            // Every instrument in the venue's list counts for books, also one it is not taking orders for right now.
            demand.Listed(listing.Venue, listing.Type, symbols.Select(s => s.Asset), listing.Quote);
            Dictionary<string, ListedInstrument> stored = await database.ListedInstruments.Where(i => i.Venue == listing.Venue && i.Type == listing.Type && i.Quote == listing.Quote).ToDictionaryAsync(i => i.Asset, stopping);
            foreach (ListedSymbol symbol in symbols)
            {
                if (!stored.Remove(symbol.Asset, out ListedInstrument? row))
                {
                    row = new ListedInstrument { Venue = listing.Venue, Type = listing.Type, Asset = symbol.Asset, Quote = listing.Quote, Symbol = symbol.Symbol };
                    database.ListedInstruments.Add(row);
                }

                row.Quote = listing.Quote;
                row.Symbol = symbol.Symbol;
                row.Tradable = symbol.Tradable;
                row.PriceStep = Kept(symbol.PriceStep);
                row.QuantityStep = Kept(symbol.QuantityStep);
                row.MinQuantity = Kept(symbol.MinQuantity);
                row.SeenAt = now;
            }

            // What the venue no longer lists stays on record but cannot be chosen.
            foreach (ListedInstrument gone in stored.Values.Where(i => i.Tradable))
            {
                gone.Tradable = false;
            }

            await database.SaveChangesAsync(stopping);
        }

        if (failed.Count > 0)
        {
            logger.LogWarning("Instrument lists read: {Total} instruments. Not read, and kept as they were: {Failed}.", total, string.Join("; ", failed));
        }
        else
        {
            logger.LogInformation("Instrument lists read: {Total} instruments on {Lists} venue markets. {Detail}.", total, answers.Length, string.Join("; ", read));
        }

        return failed.Count == 0;
    }
}

/// <summary>
/// Keeps the list of wanted books up to date: what somebody asked for in the last few minutes, and every instrument
/// with an open order or a position, which must have a book whether or not anyone is looking.
/// </summary>
public sealed class BookDemandWorker(IServiceScopeFactory scopes, IConnectionMultiplexer keyValue, BookDemand demand, ExecutionOptions options, KatalepsisFT.Application.Execution.VenueStatusBoard board, TimeProvider clock, ILogger<BookDemandWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReadHeldEvery = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        KeyValueBookFeed feed = new(keyValue);
        board.MostOnDemand = options.MostBooks;
        List<(string, InstrumentType)> held = [];
        DateTimeOffset heldRead = DateTimeOffset.MinValue;
        DateTimeOffset warned = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset now = clock.GetUtcNow();
                if (now - heldRead >= ReadHeldEvery)
                {
                    heldRead = now;
                    held = await HeldAsync(stoppingToken);
                }

                demand.Want(held, await feed.WantedAsync(now, options.BooksKeptFor));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // What was wanted a moment ago stays wanted: a book is never closed because this could not be read.
                if (clock.GetUtcNow() - warned > TimeSpan.FromMinutes(1))
                {
                    warned = clock.GetUtcNow();
                    logger.LogWarning("The list of wanted books could not be brought up to date: {Reason}", e.Message);
                }
            }

            try
            {
                await Task.Delay(Every, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<List<(string, InstrumentType)>> HeldAsync(CancellationToken stopping)
    {
        using IServiceScope scope = scopes.CreateScope();
        IFtDbContext database = scope.ServiceProvider.GetRequiredService<IFtDbContext>();
        var orders = await database.Orders.AsNoTracking().Where(o => (int)o.Status < (int)OrderStatus.Filled).Select(o => new { o.Asset, o.InstrumentType }).Distinct().ToListAsync(stopping);
        var positions = await database.TradingPositions.AsNoTracking().Where(p => p.Quantity != 0m).Select(p => new { p.Asset, Type = p.InstrumentType }).Distinct().ToListAsync(stopping);
        // A working order needs its book for as long as it is open, also between slices and while it waits for approval.
        var working = await database.WorkingOrders.AsNoTracking().Where(w => w.State == WorkingState.Working || w.State == WorkingState.Paused || w.State == WorkingState.PendingApproval).Select(w => new { w.Asset, Type = w.InstrumentType }).Distinct().ToListAsync(stopping);
        // What a fund has pinned is kept open like something held.
        var pinned = await database.PinnedInstruments.AsNoTracking().Select(p => new { p.Asset, p.Type }).Distinct().ToListAsync(stopping);
        return orders.Select(o => (o.Asset, o.InstrumentType)).Concat(positions.Select(p => (p.Asset, p.Type))).Concat(pinned.Select(p => (p.Asset, p.Type))).Concat(working.Select(p => (p.Asset, p.Type))).Distinct().ToList();
    }
}

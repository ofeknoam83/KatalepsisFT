using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Application.Execution;

/// <summary>One price in a venue's book and how much is offered there, in units of the asset.</summary>
public readonly record struct BookLevel(decimal Price, decimal Quantity);

/// <summary>One venue's book for one instrument at a moment: bids from the highest down, asks from the lowest up.</summary>
public sealed record VenueBook(Venue Venue, string Asset, InstrumentType Type, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks, DateTimeOffset At)
{
    public decimal? BestBid => Bids.Count > 0 ? Bids[0].Price : null;

    public decimal? BestAsk => Asks.Count > 0 ? Asks[0].Price : null;

    public decimal? Mid => BestBid is decimal bid && BestAsk is decimal ask ? (bid + ask) / 2m : null;
}

/// <summary>Something that can be traded right now, and the venues with a live book for it.</summary>
public sealed record LiveInstrument(string Asset, InstrumentType Type, IReadOnlyList<Venue> Venues);

/// <summary>A level of the consolidated book: a venue's price and size, placed among every other venue's.</summary>
public readonly record struct RoutedLevel(Venue Venue, decimal Price, decimal Quantity);

/// <summary>
/// Every venue's book for every instrument, side by side. Venues write into it as their streams deliver; readers get
/// one merged view. A venue whose book has not been refreshed for a few seconds is left out: quoting or routing on a
/// price that may be gone is worse than not seeing that venue.
/// </summary>
public sealed class ConsolidatedBook(TimeProvider clock)
{
    /// <summary>A venue's book older than this is not used.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(2);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Asset, InstrumentType Type, Venue Venue), VenueBook> _books = new();

    public void Update(VenueBook book) => _books[(book.Asset.ToUpperInvariant(), book.Type, book.Venue)] = book;

    /// <summary>Forgets a venue, for example when its stream drops: its last prices must not be traded on.</summary>
    public void Clear(Venue venue)
    {
        foreach ((string Asset, InstrumentType Type, Venue Venue) key in _books.Keys.Where(k => k.Venue == venue).ToList())
        {
            _books.TryRemove(key, out _);
        }
    }

    /// <summary>One venue's book for an instrument, if it is fresh.</summary>
    public VenueBook? Of(Venue venue, string asset, InstrumentType type) =>
        _books.TryGetValue((asset.ToUpperInvariant(), type, venue), out VenueBook? book) && clock.GetUtcNow() - book.At <= MaxAge ? book : null;

    /// <summary>Every venue's fresh book for an instrument.</summary>
    public IReadOnlyList<VenueBook> For(string asset, InstrumentType type)
    {
        DateTimeOffset now = clock.GetUtcNow();
        string wanted = asset.ToUpperInvariant();
        return _books.Where(b => b.Key.Asset == wanted && b.Key.Type == type && now - b.Value.At <= MaxAge).Select(b => b.Value).OrderBy(b => b.Venue).ToList();
    }

    /// <summary>All instruments that have at least one fresh book.</summary>
    public IReadOnlyList<LiveInstrument> Instruments()
    {
        DateTimeOffset now = clock.GetUtcNow();
        return _books
            .Where(b => now - b.Value.At <= MaxAge)
            .GroupBy(b => (b.Key.Asset, b.Key.Type))
            .Select(g => new LiveInstrument(g.Key.Asset, g.Key.Type, g.Select(b => b.Key.Venue).OrderBy(v => v).ToList()))
            .OrderBy(i => i.Asset)
            .ThenBy(i => i.Type)
            .ToList();
    }

    /// <summary>The side an order of this kind would trade against, across the given venues, best price first.</summary>
    public static IReadOnlyList<RoutedLevel> Opposite(IEnumerable<VenueBook> books, OrderSide side) =>
        side == OrderSide.Buy
            ? books.SelectMany(b => b.Asks.Select(l => new RoutedLevel(b.Venue, l.Price, l.Quantity))).OrderBy(l => l.Price).ToList()
            : books.SelectMany(b => b.Bids.Select(l => new RoutedLevel(b.Venue, l.Price, l.Quantity))).OrderByDescending(l => l.Price).ToList();

    /// <summary>The middle of the best bid and best ask across the given venues; null when either side is empty.</summary>
    public static decimal? Mid(IReadOnlyCollection<VenueBook> books)
    {
        decimal? bid = books.Where(b => b.BestBid is not null).Select(b => b.BestBid).Max();
        decimal? ask = books.Where(b => b.BestAsk is not null).Select(b => b.BestAsk).Min();
        return bid is decimal b && ask is decimal a ? (b + a) / 2m : null;
    }
}

/// <summary>A trade a paper order would have made: where, how much, at what price, and whether it took or added liquidity.</summary>
public readonly record struct PaperFill(Venue Venue, decimal Quantity, decimal Price, bool IsMaker);

/// <summary>
/// Fills paper orders the way a venue would, against the real book. Nothing here is random and nothing is assumed
/// about liquidity that is not displayed: an order larger than the book fills only what is shown.
/// </summary>
public static class PaperMatcher
{
    /// <summary>What an order taking liquidity fills right now, walking the book until its limit (or its price band, for a market order).</summary>
    /// <param name="worstPrice">The limit price, or for a market order the edge of its price band; nothing fills beyond it.</param>
    public static IReadOnlyList<PaperFill> Take(OrderSide side, decimal quantity, decimal? worstPrice, IReadOnlyList<RoutedLevel> opposite)
    {
        List<PaperFill> fills = [];
        decimal left = quantity;
        foreach (RoutedLevel level in opposite)
        {
            if (left <= 0m || (worstPrice is decimal limit && (side == OrderSide.Buy ? level.Price > limit : level.Price < limit)))
            {
                break;
            }

            decimal take = Math.Min(left, level.Quantity);
            if (take > 0m)
            {
                fills.Add(new PaperFill(level.Venue, take, level.Price, false));
                left -= take;
            }
        }

        return fills;
    }

    /// <summary>Whether a limit price would trade at once against this book.</summary>
    public static bool Crosses(OrderSide side, decimal limitPrice, VenueBook book) =>
        side == OrderSide.Buy ? book.BestAsk is decimal ask && limitPrice >= ask : book.BestBid is decimal bid && limitPrice <= bid;

    /// <summary>
    /// What a resting limit order fills when the market has come to it. It fills at its own price, as the maker, and
    /// only against size the other side now shows at or through that price.
    /// </summary>
    public static IReadOnlyList<PaperFill> Rest(OrderSide side, decimal remaining, decimal limitPrice, VenueBook book)
    {
        decimal available = side == OrderSide.Buy ? book.Asks.Where(l => l.Price <= limitPrice).Sum(l => l.Quantity) : book.Bids.Where(l => l.Price >= limitPrice).Sum(l => l.Quantity);
        decimal take = Math.Min(remaining, available);
        return take > 0m ? [new PaperFill(book.Venue, take, limitPrice, true)] : [];
    }
}

/// <summary>What a venue charges, as fractions of what is traded. Used for paper fills and to compare venues.</summary>
public static class VenueFees
{
    public static (decimal Maker, decimal Taker) Of(Venue venue, InstrumentType type) =>
        (venue, type) switch
        {
            (Venue.Binance, InstrumentType.Perp) => (0.0002m, 0.0005m),
            (Venue.Bybit, InstrumentType.Perp) => (0.0002m, 0.00055m),
            (Venue.Okx, InstrumentType.Perp) => (0.0002m, 0.0005m),
            (Venue.Hyperliquid, InstrumentType.Perp) => (0.00015m, 0.00045m),
            (Venue.Kucoin, InstrumentType.Perp) => (0.0002m, 0.0006m),
            (Venue.Hyperliquid, _) => (0.0004m, 0.0007m),
            _ => (0.001m, 0.001m),
        };
}

/// <summary>The arithmetic of a position: what a fill does to its size, its average price and its realised profit.</summary>
public static class PositionMath
{
    /// <param name="fillQuantity">Signed: positive for a buy, negative for a sell.</param>
    /// <returns>The new signed quantity, the new average entry price, and the profit this fill realised.</returns>
    public static (decimal Quantity, decimal AveragePrice, decimal RealisedUsd) Apply(decimal quantity, decimal averagePrice, decimal fillQuantity, decimal fillPrice)
    {
        if (quantity == 0m || Math.Sign(quantity) == Math.Sign(fillQuantity))
        {
            // Opening or adding: the average moves towards the fill.
            decimal total = quantity + fillQuantity;
            return (total, total == 0m ? 0m : (quantity * averagePrice + fillQuantity * fillPrice) / total, 0m);
        }

        decimal closed = Math.Min(Math.Abs(quantity), Math.Abs(fillQuantity));
        // A long that is sold higher, or a short that is bought back lower, makes money.
        decimal realised = closed * (fillPrice - averagePrice) * Math.Sign(quantity);
        decimal left = quantity + fillQuantity;
        // Reduced: the average stays. Flipped through zero: what is left was opened at the fill price.
        return (left, left == 0m ? 0m : Math.Sign(left) == Math.Sign(quantity) ? averagePrice : fillPrice, realised);
    }
}

/// <summary>What the checks need to know about an order's surroundings.</summary>
public sealed record CheckContext(
    TradingAccount Account,
    decimal PositionQuantity,
    VenueBook? Book,
    bool IsHalted,
    decimal? StrategyMaxOrderNotionalUsd,
    decimal OpenBuyQuantity,
    decimal OpenSellQuantity
);

/// <summary>
/// The checks every order passes before anything is done with it, whoever sent it. Each returns a sentence a person
/// can act on. They are deliberately strict: an order that is refused costs nothing; one that should have been costs money.
/// </summary>
public static class PreTradeChecks
{
    public const decimal MaxLeverage = 20m;

    /// <returns>Null when the order may proceed, otherwise why not.</returns>
    public static string? Check(Order order, CheckContext context)
    {
        TradingAccount account = context.Account;
        if (context.IsHalted)
        {
            return "Trading is halted for this account. The kill switch has to be released before it can trade.";
        }

        if (order.Quantity <= 0m)
        {
            return "The quantity must be above zero.";
        }

        if (order.Type == OrderType.Limit && order.LimitPrice is not > 0m)
        {
            return "A limit order needs a price above zero.";
        }

        if (order.Type == OrderType.Market && order.TimeInForce == TimeInForce.PostOnly)
        {
            return "A market order cannot be post-only.";
        }

        if (order.ReduceOnly && order.InstrumentType != InstrumentType.Perp)
        {
            return "Reduce-only is for perpetuals.";
        }

        if (context.Book is not { Mid: decimal mid })
        {
            return $"There is no live market for {order.Asset} on {account.Venue} at the moment, so the order cannot be priced or checked.";
        }

        decimal price = order.LimitPrice ?? mid;
        decimal band = mid * account.PriceBandBps / 10_000m;
        if (order.Type == OrderType.Limit && (order.Side == OrderSide.Buy ? price > mid + band : price < mid - band))
        {
            return $"The price is more than {account.PriceBandBps / 100m:0.##}% through the market ({mid:0.########}). That is this account's limit against mistyped prices.";
        }

        decimal notional = order.Quantity * (order.Type == OrderType.Market ? mid : price);
        if (notional > account.MaxOrderNotionalUsd)
        {
            return $"The order is worth {notional:N2} USD; this account's largest order is {account.MaxOrderNotionalUsd:N2} USD.";
        }

        if (context.StrategyMaxOrderNotionalUsd is decimal strategyLimit && notional > strategyLimit)
        {
            return $"The order is worth {notional:N2} USD; this strategy key's largest order is {strategyLimit:N2} USD.";
        }

        decimal signed = order.Side == OrderSide.Buy ? order.Quantity : -order.Quantity;
        decimal after = context.PositionQuantity + signed;
        if (order.ReduceOnly)
        {
            if (context.PositionQuantity == 0m || Math.Sign(signed) == Math.Sign(context.PositionQuantity))
            {
                return "A reduce-only order needs a position on the other side to reduce.";
            }

            // Counting the other orders already working on the same side, it must still not go through zero.
            decimal alreadyReducing = order.Side == OrderSide.Buy ? context.OpenBuyQuantity : context.OpenSellQuantity;
            if (order.Quantity + alreadyReducing > Math.Abs(context.PositionQuantity))
            {
                return $"Reduce-only orders may not exceed the position ({Math.Abs(context.PositionQuantity):0.########} {order.Asset}, with {alreadyReducing:0.########} already working).";
            }
        }
        else
        {
            // The position this order could leave if every order working on its side filled as well.
            decimal worst = order.Side == OrderSide.Buy ? context.PositionQuantity + context.OpenBuyQuantity + order.Quantity : context.PositionQuantity - context.OpenSellQuantity - order.Quantity;
            decimal worstNotional = Math.Max(Math.Abs(after), Math.Abs(worst)) * mid;
            if (worstNotional > account.MaxPositionNotionalUsd && Math.Abs(after) > Math.Abs(context.PositionQuantity))
            {
                return $"With this order the {order.Asset} position could reach {worstNotional:N2} USD; this account's largest position is {account.MaxPositionNotionalUsd:N2} USD.";
            }
        }

        if (order.InstrumentType == InstrumentType.Spot && order.Side == OrderSide.Sell && after < 0m)
        {
            return $"The account holds {context.PositionQuantity:0.########} {order.Asset}; spot cannot be sold short.";
        }

        return null;
    }
}

/// <param name="Connected">The venue's market data stream is open and has sent something in the last few seconds.</param>
/// <param name="Books">How many instruments have a live book from this venue.</param>
/// <param name="RoundTripMs">The middle of the last few measured request round trips from the execution service to the venue, in milliseconds.</param>
public sealed record VenueStatus(Venue Venue, bool Connected, int Books, double? RoundTripMs, DateTimeOffset? LastMessageAt);

/// <param name="IsLeader">This copy of the execution service is the one acting on orders.</param>
/// <param name="RoundMs">How long its last look at the open orders took.</param>
public sealed record ExecutionStatus(DateTimeOffset At, bool IsLeader, double? RoundMs, IReadOnlyList<VenueStatus> Venues);

/// <summary>What the execution service knows about its own connections. Written by its streams, read when it reports.</summary>
public sealed class VenueStatusBoard(TimeProvider clock)
{
    private const int Samples = 9;
    private readonly object _gate = new();
    private readonly Dictionary<Venue, DateTimeOffset> _lastMessage = [];
    private readonly Dictionary<Venue, Queue<double>> _roundTrips = [];

    public bool IsLeader { get; set; }

    public double? RoundMs { get; set; }

    public void Heard(Venue venue)
    {
        lock (_gate)
        {
            _lastMessage[venue] = clock.GetUtcNow();
        }
    }

    public void RoundTrip(Venue venue, double milliseconds)
    {
        lock (_gate)
        {
            if (!_roundTrips.TryGetValue(venue, out Queue<double>? samples))
            {
                _roundTrips[venue] = samples = new Queue<double>();
            }

            samples.Enqueue(milliseconds);
            while (samples.Count > Samples)
            {
                samples.Dequeue();
            }
        }
    }

    public ExecutionStatus Snapshot(ConsolidatedBook books, IReadOnlyList<Venue> venues)
    {
        DateTimeOffset now = clock.GetUtcNow();
        IReadOnlyList<LiveInstrument> instruments = books.Instruments();
        lock (_gate)
        {
            return new ExecutionStatus(
                now,
                IsLeader,
                RoundMs,
                venues
                    .Select(venue =>
                    {
                        DateTimeOffset? last = _lastMessage.TryGetValue(venue, out DateTimeOffset at) ? at : null;
                        double? middle = _roundTrips.TryGetValue(venue, out Queue<double>? samples) && samples.Count > 0 ? samples.OrderBy(s => s).ElementAt(samples.Count / 2) : null;
                        return new VenueStatus(venue, last is not null && now - last <= ConsolidatedBook.MaxAge, instruments.Count(i => i.Venues.Contains(venue)), middle is null ? null : Math.Round(middle.Value, 1), last);
                    })
                    .ToList()
            );
        }
    }
}

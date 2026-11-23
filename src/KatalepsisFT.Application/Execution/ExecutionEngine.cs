using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KatalepsisFT.Application.Execution;

/// <summary>
/// The one place where orders are acted on. Whatever sent an order (a person's ticket or a strategy), it is only
/// written down by the sender; this engine picks it up, checks it, and works it. Exactly one engine runs at a time,
/// so an account's orders are never handled by two processes.
///
/// Each order is handled on its own and saved on its own, with its events and its position in one transaction: either
/// all of a step is on record or none of it is. After a crash the engine simply runs again; an order is never in a
/// state the database does not show.
///
/// This engine works the orders of paper accounts: they are filled against the live book by <see cref="PaperMatcher"/>
/// and nothing is sent to a venue. Orders of real accounts are worked by <see cref="LiveExecution"/>.
/// </summary>
public sealed class ExecutionEngine(IFtDbContext database, ConsolidatedBook books, TimeProvider clock, ILogger<ExecutionEngine> logger)
{
    /// <summary>Looks at every open order once.</summary>
    /// <returns>How many orders changed.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<long> open = await database
            .Orders.Where(o => o.Status == OrderStatus.Received || o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Working || o.Status == OrderStatus.PartlyFilled)
            // Orders of real accounts are worked by LiveExecution, against the venue itself.
            .Where(o => o.TradingAccount.Mode == TradingMode.Paper)
            .OrderBy(o => o.Id)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return 0;
        }

        List<TradingHalt> halts = await database.TradingHalts.AsNoTracking().Where(h => h.ReleasedAt == null).ToListAsync(cancellationToken);
        int changed = 0;
        foreach (long id in open)
        {
            try
            {
                if (await HandleAsync(id, halts, cancellationToken))
                {
                    changed++;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One order that cannot be handled must not stop the others; it is tried again on the next round.
                database.DiscardChanges();
                logger.LogError(e, "Order {OrderId} could not be handled.", id);
            }
        }

        return changed;
    }

    private async Task<bool> HandleAsync(long orderId, List<TradingHalt> halts, CancellationToken cancellationToken)
    {
        database.DiscardChanges();
        Order? order = await database.Orders.Include(o => o.TradingAccount).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null || !order.IsOpen || order.TradingAccount.Mode != TradingMode.Paper)
        {
            return false;
        }

        TradingAccount account = order.TradingAccount;
        DateTimeOffset now = clock.GetUtcNow();
        bool halted = halts.Any(h => h.Scope == HaltScope.Global || (h.Scope == HaltScope.Venue && h.Venue == account.Venue) || (h.Scope == HaltScope.Account && h.TradingAccountId == account.Id));
        VenueBook? book = books.Of(account.Venue, order.Asset, order.InstrumentType);
        TradingPosition? position = await database.TradingPositions.FirstOrDefaultAsync(p => p.TradingAccountId == account.Id && p.Asset == order.Asset && p.InstrumentType == order.InstrumentType, cancellationToken);
        OrderStatus before = order.Status;
        decimal filledBefore = order.FilledQuantity;

        if (order.Status == OrderStatus.Received)
        {
            await AdmitAsync(order, account, position, book, halted, now, cancellationToken);
        }

        if (order.Status is OrderStatus.Working or OrderStatus.PartlyFilled)
        {
            if (halted)
            {
                Close(order, OrderStatus.Cancelled, OrderEventKind.Cancelled, now, "Cancelled by the kill switch.");
            }
            else if (order.CancelRequested)
            {
                Close(order, OrderStatus.Cancelled, OrderEventKind.Cancelled, now, "Cancelled on request.");
            }
            else if (book is not null && order.LimitPrice is decimal limit)
            {
                decimal workable = Workable(order, position);
                if (order.ReduceOnly && workable <= 0m)
                {
                    Close(order, OrderStatus.Cancelled, OrderEventKind.Cancelled, now, "Cancelled: there is no position left to reduce.");
                }
                else
                {
                    position = Fill(order, account, position, PaperMatcher.Rest(order.Side, workable, limit, book), now);
                }
            }
        }

        if (order.Status == before && order.FilledQuantity == filledBefore && !database.HasChanges())
        {
            return false;
        }

        order.UpdatedAt = now;
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>A new order: every check, then whatever it trades at once.</summary>
    private async Task AdmitAsync(Order order, TradingAccount account, TradingPosition? position, VenueBook? book, bool halted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (order.CancelRequested)
        {
            Close(order, OrderStatus.Cancelled, OrderEventKind.Cancelled, now, "Cancelled on request before it was worked.");
            return;
        }

        // What is already working on this instrument counts towards the limits.
        var working = await database
            .Orders.Where(o => o.TradingAccountId == account.Id && o.Asset == order.Asset && o.InstrumentType == order.InstrumentType && o.Id != order.Id && (o.Status == OrderStatus.Working || o.Status == OrderStatus.PartlyFilled))
            .Select(o => new { o.Side, Remaining = o.Quantity - o.FilledQuantity })
            .ToListAsync(cancellationToken);
        decimal? strategyLimit = order.StrategyKeyId is int keyId ? await database.StrategyKeys.Where(k => k.Id == keyId).Select(k => (decimal?)k.MaxOrderNotionalUsd).FirstOrDefaultAsync(cancellationToken) : null;
        string? refusal = PreTradeChecks.Check(
            order,
            new CheckContext(account, position?.Quantity ?? 0m, book, halted, strategyLimit, working.Where(w => w.Side == OrderSide.Buy).Sum(w => w.Remaining), working.Where(w => w.Side == OrderSide.Sell).Sum(w => w.Remaining))
        );
        if (refusal is not null)
        {
            Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, refusal);
            return;
        }

        decimal mid = book!.Mid!.Value;
        order.ArrivalPrice = mid;
        order.Status = OrderStatus.Accepted;
        order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Accepted, Price = mid, Detail = "Passed every check." });

        if (order.TimeInForce == TimeInForce.PostOnly)
        {
            if (PaperMatcher.Crosses(order.Side, order.LimitPrice!.Value, book))
            {
                Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, "A post-only order must not trade at once; this price would.");
                return;
            }

            Rest(order, now);
            return;
        }

        // A market order may go no further through the book than the account's price band allows.
        decimal band = mid * account.PriceBandBps / 10_000m;
        decimal worst = order.LimitPrice ?? (order.Side == OrderSide.Buy ? mid + band : mid - band);
        Fill(order, account, position, PaperMatcher.Take(order.Side, order.RemainingQuantity, worst, ConsolidatedBook.Opposite([book], order.Side)), now);
        if (order.Status == OrderStatus.Filled)
        {
            return;
        }

        if (order.Type == OrderType.Market || order.TimeInForce == TimeInForce.ImmediateOrCancel)
        {
            Close(order, OrderStatus.Cancelled, OrderEventKind.Cancelled, now, order.FilledQuantity > 0m ? "The rest was cancelled: the book showed no more at an acceptable price." : "Cancelled: the book showed nothing at an acceptable price.");
            return;
        }

        Rest(order, now);
    }

    private static void Rest(Order order, DateTimeOffset now)
    {
        order.Status = order.FilledQuantity > 0m ? OrderStatus.PartlyFilled : OrderStatus.Working;
        order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Working, Quantity = order.RemainingQuantity, Price = order.LimitPrice, Detail = "Resting in the book." });
    }

    /// <summary>How much of a resting order may fill now: all that is left, or for a reduce-only order no more than the position.</summary>
    private static decimal Workable(Order order, TradingPosition? position)
    {
        if (!order.ReduceOnly)
        {
            return order.RemainingQuantity;
        }

        decimal held = position?.Quantity ?? 0m;
        bool reduces = order.Side == OrderSide.Sell ? held > 0m : held < 0m;
        return reduces ? Math.Min(order.RemainingQuantity, Math.Abs(held)) : 0m;
    }

    /// <summary>Books fills on the order and on the account's position.</summary>
    private TradingPosition? Fill(Order order, TradingAccount account, TradingPosition? position, IReadOnlyList<PaperFill> fills, DateTimeOffset now)
    {
        foreach (PaperFill fill in fills)
        {
            (decimal maker, decimal taker) = VenueFees.Of(fill.Venue, order.InstrumentType);
            decimal fee = Math.Round(fill.Quantity * fill.Price * (fill.IsMaker ? maker : taker), 6);
            order.AverageFillPrice = (order.AverageFillPrice * order.FilledQuantity + fill.Price * fill.Quantity) / (order.FilledQuantity + fill.Quantity);
            order.FilledQuantity += fill.Quantity;
            order.FeesUsd += fee;
            order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Fill, Venue = fill.Venue, Quantity = fill.Quantity, Price = fill.Price, FeeUsd = fee, Detail = fill.IsMaker ? "Paper fill, resting." : "Paper fill, taking." });

            position ??= database.TradingPositions.Add(new TradingPosition { TradingAccountId = account.Id, Asset = order.Asset, InstrumentType = order.InstrumentType }).Entity;
            (decimal quantity, decimal average, decimal realised) = PositionMath.Apply(position.Quantity, position.AveragePrice, order.Side == OrderSide.Buy ? fill.Quantity : -fill.Quantity, fill.Price);
            position.Quantity = quantity;
            position.AveragePrice = average;
            position.RealisedPnlUsd += Math.Round(realised, 6);
            position.FeesUsd += fee;
            position.UpdatedAt = now;
        }

        if (fills.Count > 0)
        {
            order.Status = order.RemainingQuantity <= 0m ? OrderStatus.Filled : OrderStatus.PartlyFilled;
        }

        return position;
    }

    private static void Close(Order order, OrderStatus status, OrderEventKind kind, DateTimeOffset now, string reason)
    {
        order.Status = status;
        if (status == OrderStatus.Rejected)
        {
            order.RejectReason = reason.Length <= 500 ? reason : reason[..500];
        }

        order.Events.Add(new OrderEvent { At = now, Kind = kind, Quantity = order.RemainingQuantity, Detail = reason.Length <= 500 ? reason : reason[..500] });
    }
}

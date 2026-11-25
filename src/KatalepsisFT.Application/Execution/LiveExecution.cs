using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Risk;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KatalepsisFT.Application.Execution;

/// <summary>When each order was last asked about at its venue, so that the venue is not asked more often than is useful.</summary>
public sealed class LivePacing
{
    private readonly ConcurrentDictionary<long, DateTimeOffset> _asked = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _cancelSent = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _missingSince = new();

    /// <summary>Since when the venue has been saying it has no such order; cleared as soon as it has it again.</summary>
    public DateTimeOffset MissingSince(long orderId, DateTimeOffset now) => _missingSince.GetOrAdd(orderId, now);

    public void Seen(long orderId) => _missingSince.TryRemove(orderId, out _);

    private readonly ConcurrentDictionary<(int Account, string Asset, InstrumentType Type), int> _held = new();

    /// <summary>
    /// Whether the account reading may leave a position as it is this once, because an order of ours is open in it and
    /// its own report will explain the difference. Only a few times in a row: after that the venue's figure is taken.
    /// </summary>
    public bool MayHoldBack(int accountId, string asset, InstrumentType type) => _held.AddOrUpdate((accountId, asset, type), 1, (_, held) => held + 1) <= 3;

    public void Settled(int accountId, string asset, InstrumentType type) => _held.TryRemove((accountId, asset, type), out _);

    public bool MayAsk(long orderId, DateTimeOffset now, TimeSpan every) => Due(_asked, orderId, now, every);

    public bool MayCancel(long orderId, DateTimeOffset now, TimeSpan every) => Due(_cancelSent, orderId, now, every);

    public bool CancelWasSent(long orderId) => _cancelSent.ContainsKey(orderId);

    public void Forget(long orderId)
    {
        _asked.TryRemove(orderId, out _);
        _cancelSent.TryRemove(orderId, out _);
        _missingSince.TryRemove(orderId, out _);
    }

    private static bool Due(ConcurrentDictionary<long, DateTimeOffset> last, long orderId, DateTimeOffset now, TimeSpan every)
    {
        if (last.TryGetValue(orderId, out DateTimeOffset at) && now - at < every)
        {
            return false;
        }

        last[orderId] = now;
        return true;
    }
}

/// <summary>What there is to do for the real accounts right now.</summary>
/// <param name="OpenOrders">Open orders of real accounts, by account, oldest first.</param>
/// <param name="Accounts">Every real account. Each one's venue account is read every few seconds, orders or not.</param>
public sealed record LiveWork(IReadOnlyDictionary<int, List<long>> OpenOrders, IReadOnlyList<int> Accounts);

/// <summary>
/// Works the orders of real accounts: real orders at a real venue, with real money.
///
/// Three rules keep it safe.
///
/// An order is written down as "being sent" before it goes out. If the answer is lost, or this service stops at that
/// moment, the order is never sent again: the venue is asked what became of it, by a name of our own that the venue
/// keeps for the order.
///
/// What is on record about fills is only ever what the venue reports: how much of the order has traded, and for how
/// much. Nothing is assumed from the book.
///
/// What the account holds is read from the venue every few seconds and replaces our own arithmetic, so a trade made
/// elsewhere on the same account, a liquidation or a funding payment shows up here too.
/// </summary>
public sealed class LiveExecution(IFtDbContext database, IEnumerable<IOrderGateway> gateways, ITradingKeys keys, ConsolidatedBook books, LivePacing pacing, TimeProvider clock, ILogger<LiveExecution> logger)
{
    /// <summary>How long an order that was sent and got no answer is asked about before it is closed as never having arrived.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    /// <summary>How long a new order waits for the venue's rules for its instrument before it is refused.</summary>
    public static readonly TimeSpan RulesWait = TimeSpan.FromSeconds(10);
    /// <summary>How long a venue has to keep saying it no longer has an order it once took before the order is closed here.</summary>
    public static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AskEvery = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan CancelEvery = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ReadAccountEvery = TimeSpan.FromSeconds(5);

    /// <summary>The venues real orders can be sent to.</summary>
    public IReadOnlyList<Venue> Venues => gateways.Select(g => g.Venue).Distinct().ToList();

    public async Task<LiveWork> WorkAsync(CancellationToken cancellationToken)
    {
        var orders = await database
            .Orders.AsNoTracking()
            .Where(o => o.TradingAccount.Mode == TradingMode.Live && (o.Status == OrderStatus.Received || o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Working || o.Status == OrderStatus.PartlyFilled))
            .OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.TradingAccountId })
            .ToListAsync(cancellationToken);
        List<int> accounts = await database.TradingAccounts.AsNoTracking().Where(a => a.Mode == TradingMode.Live).Select(a => a.Id).ToListAsync(cancellationToken);
        return new LiveWork(orders.GroupBy(o => o.TradingAccountId).ToDictionary(g => g.Key, g => g.Select(o => o.Id).ToList()), accounts);
    }

    /// <summary>Takes one order one step further.</summary>
    /// <returns>True when something about the order changed.</returns>
    public async Task<bool> HandleAsync(long orderId, CancellationToken cancellationToken)
    {
        database.DiscardChanges();
        Order? order = await database.Orders.Include(o => o.TradingAccount).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null || !order.IsOpen || order.TradingAccount.Mode != TradingMode.Live)
        {
            pacing.Forget(orderId);
            return false;
        }

        TradingAccount account = order.TradingAccount;
        DateTimeOffset now = clock.GetUtcNow();
        string venue = VenueExposureService.NameOf(account.Venue);
        IOrderGateway? gateway = gateways.FirstOrDefault(g => g.Venue == account.Venue);
        bool halted = await database.TradingHalts.AnyAsync(
            h => h.ReleasedAt == null && (h.Scope == HaltScope.Global || (h.Scope == HaltScope.Venue && h.Venue == account.Venue) || (h.Scope == HaltScope.Account && h.TradingAccountId == account.Id)),
            cancellationToken
        );
        int eventsBefore = order.Events.Count;
        OrderStatus before = order.Status;

        if (order.Status == OrderStatus.Received)
        {
            if (order.CancelRequested)
            {
                Close(order, OrderStatus.Cancelled, OrderEventKind.Cancelled, now, "Cancelled on request before it was sent.");
            }
            else if (gateway is null)
            {
                Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, $"Real orders cannot be sent to {venue} yet.");
            }
            else if (account.EncryptedKey is null)
            {
                Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, "The account's trading key cannot be opened, so the order was not sent.");
            }
            else if (!keys.Ready)
            {
                // Just after a start the key vault may still be opening. Nothing has been sent, so the order waits a
                // moment for it rather than being turned away; but it must not sit unsent for long.
                if (now - order.CreatedAt <= RulesWait)
                {
                    return false;
                }

                Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, "The account's trading key cannot be opened, so the order was not sent.");
            }
            else
            {
                await SendAsync(order, account, gateway, venue, halted, now, cancellationToken);
            }
        }
        else if (gateway is null || !keys.Ready || account.EncryptedKey is null)
        {
            // Sent earlier, and now there is no way to ask about it. Leave it as it is; it needs a person.
            return false;
        }
        else
        {
            await FollowAsync(order, account, gateway, venue, halted, now, cancellationToken);
        }

        if (order.Status == before && order.Events.Count == eventsBefore && !database.HasChanges())
        {
            return false;
        }

        order.UpdatedAt = now;
        await database.SaveChangesAsync(cancellationToken);
        if (!order.IsOpen)
        {
            pacing.Forget(order.Id);
        }

        return true;
    }

    /// <summary>A new order: every check, the venue's own rules, then out it goes.</summary>
    private async Task SendAsync(Order order, TradingAccount account, IOrderGateway gateway, string venue, bool halted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        VenueBook? book = books.Of(account.Venue, order.Asset, order.InstrumentType);
        TradingPosition? position = await database.TradingPositions.FirstOrDefaultAsync(p => p.TradingAccountId == account.Id && p.Asset == order.Asset && p.InstrumentType == order.InstrumentType, cancellationToken);
        var working = await database
            .Orders.Where(o => o.TradingAccountId == account.Id && o.Asset == order.Asset && o.InstrumentType == order.InstrumentType && o.Id != order.Id && (o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Working || o.Status == OrderStatus.PartlyFilled))
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

        VenueInstrument? instrument;
        try
        {
            instrument = await gateway.InstrumentAsync(order.Asset, order.InstrumentType, cancellationToken);
        }
        catch (VenueUnclearException e)
        {
            // Nothing has been sent, so waiting a moment is safe; but an order must not sit unsent for long.
            if (now - order.CreatedAt > RulesWait)
            {
                Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, $"{venue} could not be reached for its trading rules ({e.Message}), so the order was not sent.");
            }

            return;
        }

        if (instrument is null)
        {
            Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, $"{venue} does not list {order.Asset} as {(order.InstrumentType == InstrumentType.Spot ? "spot against USDT" : "a USDT perpetual")}.");
            return;
        }

        if (!instrument.Tradable)
        {
            Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, $"{venue} is not taking orders for {order.Asset} at the moment: it is suspended or being delisted there.");
            return;
        }

        decimal mid = book!.Mid!.Value;
        decimal quantity = instrument.FloorQuantity(order.Quantity);
        if (quantity <= 0m || quantity < instrument.MinQuantity)
        {
            Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, $"{venue} trades {order.Asset} in steps of {instrument.QuantityStep:0.##########} with a smallest order of {Math.Max(instrument.MinQuantity, instrument.QuantityStep):0.##########}; {order.Quantity:0.##########} is too small.");
            return;
        }

        // A market order has no price of its own, and a venue will fill it at whatever the book shows. It goes out
        // instead as an order to trade now and cancel the rest, no further through the market than the account allows.
        decimal band = mid * account.PriceBandBps / 10_000m;
        bool atMarket = order.Type == OrderType.Market;
        decimal price = instrument.RoundPrice(order.LimitPrice ?? (order.Side == OrderSide.Buy ? mid + band : mid - band), order.Side);
        if (price <= 0m)
        {
            Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, "The price does not fit the venue's price steps.");
            return;
        }

        if (quantity * price < instrument.MinNotional)
        {
            Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, $"{venue}'s smallest order for {order.Asset} is worth {instrument.MinNotional:0.########}; this one is worth {quantity * price:0.########}.");
            return;
        }

        VenueCredentials credentials;
        try
        {
            credentials = OpenKey(account);
        }
        catch (VenueUnclearException)
        {
            Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, "The account's trading key cannot be opened, so the order was not sent.");
            return;
        }

        if (quantity != order.Quantity)
        {
            order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Note, Quantity = quantity, Detail = $"Quantity brought down from {order.Quantity:0.##########} to fit the venue's steps of {instrument.QuantityStep:0.##########}." });
            order.Quantity = quantity;
        }

        order.ArrivalPrice = mid;
        order.Status = OrderStatus.Accepted;
        order.SentAt = now;
        order.VenueClientId = ClientIdFor(order);
        order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Accepted, Venue = account.Venue, Quantity = quantity, Price = price, Detail = $"Passed every check. Being sent to {venue}{(atMarket ? ", to trade now at no worse than this price" : string.Empty)}." });
        order.UpdatedAt = now;
        // On record before it leaves: from here on the order is never sent a second time, whatever happens.
        await database.SaveChangesAsync(cancellationToken);

        VenueOrderRequest request = new(order.VenueClientId, order.InstrumentType, order.Side, OrderType.Limit, atMarket ? TimeInForce.ImmediateOrCancel : order.TimeInForce, quantity, price, order.ReduceOnly, account.Leverage, account.MarginMode);
        DateTimeOffset answered;
        try
        {
            order.VenueOrderId = await gateway.PlaceAsync(credentials, instrument, request, cancellationToken);
            answered = clock.GetUtcNow();
            order.Status = OrderStatus.Working;
            order.Events.Add(new OrderEvent { At = answered, Kind = OrderEventKind.Working, Venue = account.Venue, Quantity = quantity, Price = price, Detail = $"Taken by {venue} in {(answered - now).TotalMilliseconds:0} ms." });
        }
        catch (VenueRefusedException e)
        {
            answered = clock.GetUtcNow();
            Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, answered, $"{venue} refused the order: {e.Message}");
        }
        catch (VenueUnclearException e)
        {
            logger.LogWarning("Order {OrderId} was sent to {Venue} and got no clear answer: {Reason}", order.Id, venue, e.Message);
            order.Events.Add(new OrderEvent { At = clock.GetUtcNow(), Kind = OrderEventKind.Note, Venue = account.Venue, Detail = Cut($"No clear answer from {venue} ({e.Message}). It is not sent again; {venue} is being asked what became of it.") });
        }
    }

    /// <summary>An order that has gone out: ask the venue how it stands, and pass on a cancel.</summary>
    private async Task FollowAsync(Order order, TradingAccount account, IOrderGateway gateway, string venue, bool halted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (order.VenueClientId is null)
        {
            return;
        }

        VenueCredentials credentials;
        try
        {
            credentials = OpenKey(account);
        }
        catch (VenueUnclearException)
        {
            // Without its key the order can be neither asked about nor cancelled. It stays as it is and needs a person.
            return;
        }

        VenueInstrument? instrument = await RulesAsync(gateway, order, cancellationToken);
        if (instrument is null)
        {
            return;
        }

        bool unanswered = order.Status == OrderStatus.Accepted;
        bool first = !pacing.CancelWasSent(order.Id);
        if (!unanswered && (halted || order.CancelRequested) && pacing.MayCancel(order.Id, now, CancelEvery))
        {
            try
            {
                await gateway.CancelAsync(credentials, instrument, order.InstrumentType, new OrderNames(order.VenueClientId, order.VenueOrderId), cancellationToken);
                if (first)
                {
                    order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.CancelRequested, Venue = account.Venue, Detail = halted ? $"The kill switch is on: cancel sent to {venue}." : $"Cancel sent to {venue}." });
                }
            }
            catch (VenueRefusedException)
            {
                // Usually because it has just finished. What it is now is asked below.
            }
            catch (VenueUnclearException e)
            {
                logger.LogWarning("The cancel of order {OrderId} got no clear answer from {Venue}: {Reason}", order.Id, venue, e.Message);
            }
        }

        if (!pacing.MayAsk(order.Id, now, AskEvery))
        {
            return;
        }

        VenueOrderReport report;
        try
        {
            report = await gateway.FindAsync(credentials, instrument, order.InstrumentType, new OrderNames(order.VenueClientId, order.VenueOrderId), cancellationToken);
        }
        catch (Exception e) when (e is VenueUnclearException or VenueRefusedException)
        {
            // Not knowing is not a reason to change anything. It is asked again shortly.
            logger.LogWarning("{Venue} gave no usable answer about order {OrderId}: {Reason}", venue, order.Id, e.Message);
            return;
        }

        if (report.State == VenueOrderState.NotFound)
        {
            if (unanswered)
            {
                if (now - order.SentAt > GiveUpAfter)
                {
                    Close(order, OrderStatus.Rejected, OrderEventKind.Rejected, now, $"{venue} has no such order {(now - order.SentAt!.Value).TotalSeconds:0} seconds after it was sent: it never arrived. Nothing was traded.");
                }
            }
            else if (now - pacing.MissingSince(order.Id, now) >= GoneAfter)
            {
                // The venue took this order and now, asked several times over, no longer has it. Venues drop orders
                // that were cancelled with nothing (more) filled, some within minutes. What had filled is on record.
                string how = pacing.CancelWasSent(order.Id) ? halted ? "Cancelled by the kill switch." : "Cancelled on request." : $"Cancelled at {venue}: it no longer has the order.";
                Close(order, OrderStatus.Cancelled, OrderEventKind.Cancelled, now, order.FilledQuantity > 0m ? how : how + " Nothing was filled.");
            }

            return;
        }

        pacing.Seen(order.Id);

        if (unanswered)
        {
            order.Status = OrderStatus.Working;
            order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Working, Venue = account.Venue, Quantity = order.RemainingQuantity, Detail = $"Found at {venue}: it did arrive." });
        }

        order.VenueOrderId ??= report.VenueOrderId;
        await ApplyAsync(order, account, report, venue, halted, now, cancellationToken);
    }

    /// <summary>Brings the order's record up to what the venue reports.</summary>
    private async Task ApplyAsync(Order order, TradingAccount account, VenueOrderReport report, string venue, bool halted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        decimal traded = report.FilledQuantity - order.FilledQuantity;
        // Fees are kept to six decimals, so the venue's figure is compared at that precision: otherwise a fee of
        // 0.03306787 would look different from the 0.033068 on record at every look, for ever.
        decimal? venueFee = report.FeeUsd is decimal stated ? Math.Round(stated, 6) : null;
        TradingPosition? position = null;
        if (traded > 0m || (venueFee is decimal known && known != order.FeesUsd))
        {
            position = await database.TradingPositions.FirstOrDefaultAsync(p => p.TradingAccountId == account.Id && p.Asset == order.Asset && p.InstrumentType == order.InstrumentType, cancellationToken);
        }

        if (traded > 0m)
        {
            decimal value = report.FilledValue - order.AverageFillPrice * order.FilledQuantity;
            decimal price = value > 0m ? value / traded : report.FilledQuantity > 0m ? report.FilledValue / report.FilledQuantity : order.LimitPrice ?? 0m;
            // The venue's own figure where it gives one; until then the venue's usual rate, corrected later.
            // Where the venue's total is lower than what was estimated for earlier fills, this fill carries the
            // correction, so the order always ends on the venue's own total.
            decimal fee = venueFee is decimal total ? total - order.FeesUsd : Math.Round(traded * price * VenueFees.Of(account.Venue, order.InstrumentType).Taker, 6);
            order.AverageFillPrice = report.FilledValue > 0m ? report.FilledValue / report.FilledQuantity : price;
            order.FilledQuantity = report.FilledQuantity;
            order.FeesUsd += fee;
            order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Fill, Venue = account.Venue, Quantity = traded, Price = price, FeeUsd = fee, Detail = report.FeeUsd is null ? $"Filled at {venue}. Fee estimated until {venue} reports it." : $"Filled at {venue}." });

            position ??= database.TradingPositions.Add(new TradingPosition { TradingAccountId = account.Id, Asset = order.Asset, InstrumentType = order.InstrumentType }).Entity;
            (decimal quantity, decimal average, decimal realised) = PositionMath.Apply(position.Quantity, position.AveragePrice, order.Side == OrderSide.Buy ? traded : -traded, price);
            position.Quantity = quantity;
            position.AveragePrice = average;
            position.RealisedPnlUsd += Math.Round(realised, 6);
            position.FeesUsd += fee;
            position.UpdatedAt = now;
            order.Status = OrderStatus.PartlyFilled;
        }
        else if (venueFee is decimal total && total != order.FeesUsd)
        {
            if (position is not null)
            {
                position.FeesUsd += total - order.FeesUsd;
            }

            order.FeesUsd = total;
            order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Note, Venue = account.Venue, FeeUsd = total, Detail = $"Fee as {venue} reports it." });
        }

        if (report.State != VenueOrderState.Done)
        {
            return;
        }

        if (order.RemainingQuantity <= 0m)
        {
            order.Status = OrderStatus.Filled;
            return;
        }

        string why = pacing.CancelWasSent(order.Id)
            ? halted ? "Cancelled by the kill switch." : "Cancelled on request."
            : order.Type == OrderType.Market || order.TimeInForce == TimeInForce.ImmediateOrCancel ? $"The rest was cancelled: {venue} had no more at an acceptable price."
            : order.TimeInForce == TimeInForce.PostOnly && order.FilledQuantity == 0m ? $"{venue} cancelled it: a maker-only order that would have traded at once."
            : $"Cancelled at {venue}{(report.Note is null ? string.Empty : ": " + report.Note)}.";
        Close(order, OrderStatus.Cancelled, OrderEventKind.Cancelled, now, why);
    }

    /// <summary>
    /// Reads what the account holds at the venue and makes that our record. Positions the venue no longer reports
    /// are set to nothing. A reading that fails changes nothing and is put on the account for people to see.
    /// </summary>
    public async Task ReadAccountAsync(int accountId, CancellationToken cancellationToken)
    {
        database.DiscardChanges();
        TradingAccount? account = await database.TradingAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null || account.Mode != TradingMode.Live)
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();
        IOrderGateway? gateway = gateways.FirstOrDefault(g => g.Venue == account.Venue);
        try
        {
            if (gateway is null)
            {
                throw new VenueUnclearException("real orders cannot be sent to this venue");
            }

            VenueAccountReport report = await gateway.AccountAsync(OpenKey(account), cancellationToken);
            // The reading took a moment. If the account was taken back to paper meanwhile, what the venue holds is no
            // longer this account's business and must not be written onto it.
            if (!await database.TradingAccounts.AsNoTracking().AnyAsync(a => a.Id == accountId && a.Mode == TradingMode.Live, cancellationToken))
            {
                return;
            }

            List<TradingPosition> positions = await database.TradingPositions.Where(p => p.TradingAccountId == accountId).ToListAsync(cancellationToken);
            if (!report.PerpsRead && positions.Any(p => p.InstrumentType == InstrumentType.Perp && p.Quantity != 0m))
            {
                // Without the futures side the perpetuals on record can be neither confirmed nor cleared. Treating
                // them as gone would open the position limit and break reduce-only orders, so nothing is changed.
                throw new VenueUnclearException("the futures side of the account could not be read");
            }

            List<VenueHolding> holdings = Netted(report.Holdings);
            // Where an order of ours is still open, a quantity that differs from the record is most likely a fill the
            // order's own report has not brought yet. That report books the fill against the position as it was, which
            // is what gives the right profit; taking the venue's quantity first would lose it. So such a position
            // waits a moment for the order.
            HashSet<(string Asset, InstrumentType Type)> working = (await database.Orders.AsNoTracking()
                .Where(o => o.TradingAccountId == accountId && (o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Working || o.Status == OrderStatus.PartlyFilled))
                .Select(o => new { o.Asset, o.InstrumentType })
                .ToListAsync(cancellationToken)).Select(o => (o.Asset, o.InstrumentType)).ToHashSet();
            decimal equity = report.CashUsd + (report.DerivativesEquityUsd ?? 0m);
            foreach (VenueHolding holding in holdings)
            {
                decimal? mark = books.Of(account.Venue, holding.Asset, holding.Type)?.Mid ?? holding.PriceUsd ?? holding.AveragePrice;
                if (holding.Type == InstrumentType.Spot)
                {
                    equity += holding.Quantity * (mark ?? 0m);
                }

                TradingPosition? position = positions.FirstOrDefault(p => p.Asset == holding.Asset && p.InstrumentType == holding.Type);
                if (position is null)
                {
                    if (holding.Quantity == 0m)
                    {
                        continue;
                    }

                    position = database.TradingPositions.Add(new TradingPosition { TradingAccountId = accountId, Asset = holding.Asset, InstrumentType = holding.Type, UpdatedAt = now }).Entity;
                    positions.Add(position);
                }

                if (position.Quantity == holding.Quantity)
                {
                    pacing.Settled(accountId, holding.Asset, holding.Type);
                }
                else if (working.Contains((holding.Asset, holding.Type)) && pacing.MayHoldBack(accountId, holding.Asset, holding.Type))
                {
                    continue;
                }
                else
                {
                    pacing.Settled(accountId, holding.Asset, holding.Type);
                }

                if (position.Quantity != holding.Quantity || position.LiquidationPrice != holding.LiquidationPrice)
                {
                    position.UpdatedAt = now;
                }

                // A perpetual's entry price is the venue's. Spot has none there: ours is kept, and something first
                // seen here (bought before, or elsewhere) is taken at today's price.
                position.AveragePrice = holding.Type == InstrumentType.Perp ? holding.AveragePrice ?? position.AveragePrice : position.AveragePrice > 0m && position.Quantity != 0m ? position.AveragePrice : mark ?? 0m;
                position.Quantity = holding.Quantity;
                position.LiquidationPrice = holding.LiquidationPrice;
            }

            foreach (TradingPosition gone in positions.Where(p => p.Quantity != 0m && !holdings.Any(h => h.Asset == p.Asset && h.Type == p.InstrumentType)))
            {
                if (working.Contains((gone.Asset, gone.InstrumentType)) && pacing.MayHoldBack(accountId, gone.Asset, gone.InstrumentType))
                {
                    continue;
                }

                pacing.Settled(accountId, gone.Asset, gone.InstrumentType);
                gone.Quantity = 0m;
                gone.LiquidationPrice = null;
                gone.UpdatedAt = now;
            }

            // A reading without the futures side leaves that wallet out. The account's value is then kept as it was
            // last read whole, so that it does not drop and come back in the fund's NAV.
            if (report.PerpsRead || account.LiveEquityUsd is null)
            {
                account.LiveEquityUsd = Math.Round(equity, 2);
            }

            account.LiveSyncedAt = now;
            account.LiveSyncError = null;
        }
        catch (Exception e) when (e is VenueUnclearException or VenueRefusedException)
        {
            database.DiscardChanges();
            account = await database.TradingAccounts.FirstAsync(a => a.Id == accountId, cancellationToken);
            account.LiveSyncError = Cut(e.Message);
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One holding per asset and type. An account in hedge mode has a long and a short leg of the same perpetual; our
    /// record has one position, so the two are added up: the net quantity, at the larger leg's entry price. The
    /// liquidation price of either leg alone would mislead, so it is left out.
    /// </summary>
    private static List<VenueHolding> Netted(IReadOnlyList<VenueHolding> holdings) =>
        holdings
            .GroupBy(h => (h.Asset, h.Type))
            .Select(g =>
            {
                List<VenueHolding> legs = g.ToList();
                if (legs.Count == 1)
                {
                    return legs[0];
                }

                VenueHolding larger = legs.OrderByDescending(l => Math.Abs(l.Quantity)).First();
                return new VenueHolding(g.Key.Asset, g.Key.Type, legs.Sum(l => l.Quantity), larger.AveragePrice, null, larger.PriceUsd);
            })
            .ToList();

    /// <summary>
    /// The name the venue knows the order by: ours, the same every time for the same order, and different for
    /// orders of another installation that happens to use the same venue account.
    /// </summary>
    public static string ClientIdFor(Order order)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{order.TradingAccountId}|{order.ClientOrderId}|{order.CreatedAt.UtcTicks}"));
        // Letters and digits only, and short: the strictest venue allows nothing else and no more than 28 of them.
        return $"kft{order.Id}x{Convert.ToHexString(hash, 0, 6).ToLowerInvariant()}";
    }

    /// <summary>The account's trading key, opened. Whatever is wrong with it, it comes out as one thing: it cannot be used right now.</summary>
    private VenueCredentials OpenKey(TradingAccount account)
    {
        if (!keys.Ready || account.EncryptedKey is null)
        {
            throw new VenueUnclearException("the account's trading key cannot be opened");
        }

        try
        {
            return keys.Open(account.EncryptedKey);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            throw new VenueUnclearException("the account's trading key cannot be opened", e);
        }
    }

    private readonly Dictionary<(Venue, string, InstrumentType), VenueInstrument?> _rules = [];

    private async Task<VenueInstrument?> RulesAsync(IOrderGateway gateway, Order order, CancellationToken cancellationToken)
    {
        if (_rules.TryGetValue((gateway.Venue, order.Asset, order.InstrumentType), out VenueInstrument? known))
        {
            return known;
        }

        try
        {
            return _rules[(gateway.Venue, order.Asset, order.InstrumentType)] = await gateway.InstrumentAsync(order.Asset, order.InstrumentType, cancellationToken);
        }
        catch (VenueUnclearException)
        {
            return null;
        }
    }

    private static void Close(Order order, OrderStatus status, OrderEventKind kind, DateTimeOffset now, string reason)
    {
        order.Status = status;
        if (status == OrderStatus.Rejected)
        {
            order.RejectReason = Cut(reason);
        }

        order.Events.Add(new OrderEvent { At = now, Kind = kind, Quantity = order.RemainingQuantity, Detail = Cut(reason) });
    }

    private static string Cut(string text) => text.Length <= 500 ? text : text[..500];
}

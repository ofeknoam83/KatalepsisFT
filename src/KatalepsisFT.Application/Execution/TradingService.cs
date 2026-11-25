using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Risk;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application.Execution;

// ---------------------------------------------------------------- what the rest of the system provides

/// <summary>The consolidated book as the web service sees it: published by the execution service, read here.</summary>
public interface IBookFeed
{
    Task<IReadOnlyList<VenueBook>> ReadAsync(string asset, InstrumentType type, CancellationToken cancellationToken);

    Task<IReadOnlyList<LiveInstrument>> InstrumentsAsync(CancellationToken cancellationToken);

    /// <summary>How the execution service is doing, or null when it has not been heard from in the last few seconds.</summary>
    Task<ExecutionStatus?> StatusAsync(CancellationToken cancellationToken);
}

/// <summary>Tells the execution service there is something new to look at, so it does not wait for its next round.</summary>
public interface IExecutionSignal
{
    Task WakeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The vault for trading keys. The web service can only seal a key (encrypt it for the execution service); only the
/// execution service can open one. A stolen copy of the web service's secrets does not give up the keys.
/// </summary>
public interface ITradingVault
{
    /// <summary>False until the execution service has run once and published its public key.</summary>
    Task<bool> CanSealAsync(CancellationToken cancellationToken);

    Task<string> SealAsync(VenueCredentials credentials, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------- shapes

/// <param name="LiquidationPrice">For a leveraged position on a real account: where the venue would close it by force.</param>
/// <param name="LiquidationDistance">How far the market is from that price, as a fraction of the market price.</param>
public sealed record TradingPositionDto(string Asset, InstrumentType Type, decimal Quantity, decimal AveragePrice, decimal? MarkPrice, decimal? NotionalUsd, decimal? UnrealisedPnlUsd, decimal RealisedPnlUsd, decimal FeesUsd, decimal? LiquidationPrice = null, decimal? LiquidationDistance = null);

public sealed record TradingAccountDto(
    int Id,
    int ManagerId,
    string Manager,
    string Sleeve,
    Venue Venue,
    string VenueName,
    string Label,
    TradingMode Mode,
    bool HasKey,
    string? KeyHint,
    decimal Leverage,
    MarginMode MarginMode,
    decimal MaxOrderNotionalUsd,
    decimal MaxPositionNotionalUsd,
    int PriceBandBps,
    decimal PaperBalanceUsd,
    decimal EquityUsd,
    bool IsHalted,
    IReadOnlyList<TradingPositionDto> Positions,
    bool CanGoLive = false,
    DateTimeOffset? LiveReadAt = null,
    string? LiveReadError = null,
    string PerpQuote = "USDT"
);

public sealed record HaltDto(int Id, HaltScope Scope, Venue? Venue, int? TradingAccountId, string Where, string Reason, string PulledBy, DateTimeOffset PulledAt, bool ReleaseRequested);

public sealed record ApprovalDto(int Id, ApprovalKind Kind, string Summary, string RequestedBy, DateTimeOffset RequestedAt, ApprovalStatus Status, string? DecidedBy, DateTimeOffset? DecidedAt, string? Note, bool CanDecide, bool CanCancel);

public sealed record VenueOption(Venue Venue, string Name, bool NeedsPassphrase);

public sealed record ManagerOption(int Id, string Name, string Sleeve);

public sealed record TradingDto(
    int FundId,
    string FundName,
    bool IsDemo,
    bool CanTrade,
    bool CanAdmin,
    bool VaultReady,
    IReadOnlyList<TradingAccountDto> Accounts,
    IReadOnlyList<HaltDto> Halts,
    IReadOnlyList<ApprovalDto> Approvals,
    IReadOnlyList<VenueOption> Venues,
    IReadOnlyList<ManagerOption> Managers,
    IReadOnlyList<LiveInstrument> Instruments,
    ExecutionStatus? Engine,
    IReadOnlyList<InstrumentRef> Pinned,
    IReadOnlyList<InstrumentRef> Recent,
    bool PaperInBook = false
);

public sealed record PaperInBookRequest(bool Include);

/// <summary>An instrument by name: the asset and the kind of market.</summary>
public sealed record InstrumentRef(string Asset, InstrumentType Type);

/// <summary>What one venue's list says about an instrument: whether it takes orders, and its smallest steps.</summary>
public sealed record VenueRules(Venue Venue, string Quote, bool Tradable, decimal? PriceStep, decimal? QuantityStep, decimal? MinQuantity);

public sealed record OrderEventDto(DateTimeOffset At, OrderEventKind Kind, Venue? Venue, decimal? Quantity, decimal? Price, decimal? FeeUsd, string? Detail);

/// <param name="SlippageBps">How much worse (positive) or better (negative) than the market at arrival the fills were, in hundredths of a percent.</param>
public sealed record OrderDto(
    long Id,
    string ClientOrderId,
    int TradingAccountId,
    string Account,
    Venue Venue,
    string Asset,
    InstrumentType InstrumentType,
    OrderSide Side,
    OrderType Type,
    TimeInForce TimeInForce,
    decimal Quantity,
    decimal? LimitPrice,
    bool ReduceOnly,
    OrderStatus Status,
    decimal FilledQuantity,
    decimal AverageFillPrice,
    decimal FeesUsd,
    decimal? ArrivalPrice,
    decimal? SlippageBps,
    string? RejectReason,
    bool CancelRequested,
    OrderSource Source,
    string SubmittedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OrderEventDto> Events
);

public sealed record BookVenueDto(Venue Venue, string VenueName, decimal? BestBid, decimal? BestAsk, decimal? BidSize, decimal? AskSize, double AgeMs, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks, string Quote = "USDT");

public sealed record BookDto(string Asset, InstrumentType Type, decimal? BestBid, Venue? BestBidVenue, decimal? BestAsk, Venue? BestAskVenue, decimal? Mid, decimal? SpreadBps, IReadOnlyList<BookVenueDto> Venues);

/// <param name="PerpQuote">What the account's perpetuals are margined in; USDT when left out.</param>
public sealed record SaveTradingAccountRequest(int ManagerId, Venue Venue, string Label, decimal Leverage, MarginMode MarginMode, decimal MaxOrderNotionalUsd, decimal MaxPositionNotionalUsd, int PriceBandBps, decimal PaperBalanceUsd, string? PerpQuote = null);

public sealed record TradingKeyRequest(string ApiKey, string ApiSecret, string? Passphrase);

/// <param name="TradingAccountId">The account to trade on; or leave it out and give <paramref name="ManagerId"/> to use the manager's account with the best price.</param>
/// <param name="ClientOrderId">The sender's own name for the order; sending the same name again returns the same order instead of placing another.</param>
public sealed record SubmitOrderRequest(
    int? TradingAccountId,
    int? ManagerId,
    string Asset,
    InstrumentType InstrumentType,
    OrderSide Side,
    OrderType Type,
    TimeInForce TimeInForce,
    decimal Quantity,
    decimal? LimitPrice,
    bool ReduceOnly,
    string? ClientOrderId
);

public sealed record PullHaltRequest(HaltScope Scope, Venue? Venue, int? TradingAccountId, string Reason);

public sealed record DecisionRequest(string? Note);

public sealed record StrategyKeyDto(int Id, string Name, string Prefix, IReadOnlyList<int> AccountIds, decimal MaxOrderNotionalUsd, bool IsEnabled, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <param name="Secret">The key itself. Shown this once; only its hash is kept.</param>
public sealed record StrategyKeyCreatedDto(StrategyKeyDto Key, string Secret);

public sealed record SaveStrategyKeyRequest(string Name, IReadOnlyList<int> AccountIds, decimal MaxOrderNotionalUsd);

/// <summary>Who is acting: their name as it goes on record, and what they may do.</summary>
public sealed record Actor(string Name, bool CanTrade, bool IsAdmin);

/// <summary>The outcome of a change: done (perhaps with something to show), refused with a reason, or about something that is not there.</summary>
public sealed record TradingChange<T>(T? Value, string? Error, bool NotFound = false, string? Notice = null)
{
    public bool Done => Error is null && !NotFound;

    public static TradingChange<T> Ok(T? value = default, string? notice = null) => new(value, null, false, notice);

    public static TradingChange<T> Refused(string error) => new(default, error);

    public static TradingChange<T> Missing { get; } = new(default, null, true);
}

// ---------------------------------------------------------------- the service

/// <summary>
/// Everything the web service does on the execution side: trading accounts, their keys and limits, kill switches,
/// approvals, and taking in orders. It never fills or sends an order itself; it writes the order down and wakes the
/// execution service, which is the only thing that acts on one.
/// </summary>
/// <summary>An instrument found in the venues' lists, and the venues that have it.</summary>
public sealed record InstrumentHit(string Asset, InstrumentType Type, IReadOnlyList<Venue> Venues);

public sealed class TradingService(IFtDbContext database, IBookFeed books, IExecutionSignal signal, ITradingVault vault, VenueAdapterRegistry adapters, TimeProvider clock)
{
    /// <summary>The venues orders can go to in this version.</summary>
    public static readonly IReadOnlyList<Venue> Venues = [Venue.Binance, Venue.Bybit, Venue.Okx, Venue.Hyperliquid, Venue.Kucoin, Venue.Bitget, Venue.Gate, Venue.Mexc, Venue.CoinbaseExchange, Venue.Kraken, Venue.Deribit];

    public const string DemoIsFixed = "The example fund cannot trade.";
    public const int MaxOrdersListed = 300;

    /// <summary>
    /// Venues that offer no way to ask what a key is allowed to do. A trading key for one of them is accepted once it
    /// works, as Katalepsis accepts it, and the request for approval says in so many words that its permissions were
    /// not checked. Everywhere else a key that cannot be shown to be unable to withdraw is refused.
    /// </summary>
    /// <summary>How many instruments one fund may keep pinned. Each one is a book held open on every venue.</summary>
    public const int MostPins = 12;

    /// <summary>Venues that also list perpetuals margined in USDC, which an account can be set to trade instead of the USDT ones.</summary>
    public static readonly IReadOnlyList<Venue> UsdcPerpVenues = [Venue.Binance, Venue.Bybit];

    public static readonly IReadOnlyList<Venue> UncheckableVenues = [Venue.CoinbaseExchange, Venue.Kraken, Venue.Gate, Venue.Mexc];

    /// <summary>The venues real orders can be sent to. The others trade on paper only.</summary>
    public static readonly IReadOnlyList<Venue> LiveVenues = [Venue.Kucoin, Venue.Binance, Venue.Bybit, Venue.Okx, Venue.Bitget, Venue.Deribit, Venue.CoinbaseExchange, Venue.Mexc, Venue.Gate, Venue.Kraken];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- reading

    public async Task<TradingDto?> GetAsync(int fundId, Actor actor, CancellationToken cancellationToken)
    {
        Fund? fund = await database.Funds.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fundId, cancellationToken);
        if (fund is null)
        {
            return null;
        }

        List<TradingAccount> accounts = await database
            .TradingAccounts.AsNoTracking()
            .Include(a => a.ManagerAccount)
            .ThenInclude(m => m.Sleeve)
            .Where(a => a.ManagerAccount.Sleeve.FundId == fundId)
            .OrderBy(a => a.ManagerAccount.SleeveId)
            .ThenBy(a => a.ManagerAccountId)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        int[] accountIds = accounts.Select(a => a.Id).ToArray();
        List<TradingPosition> positions = await database.TradingPositions.AsNoTracking().Where(p => accountIds.Contains(p.TradingAccountId)).ToListAsync(cancellationToken);
        List<TradingHalt> halts = await database.TradingHalts.AsNoTracking().Where(h => h.ReleasedAt == null).OrderByDescending(h => h.PulledAt).ToListAsync(cancellationToken);
        List<ApprovalRequest> approvals = await database
            .ApprovalRequests.AsNoTracking()
            .Where(r => r.FundId == fundId || (r.FundId == null && r.Kind == ApprovalKind.ReleaseHalt))
            .OrderByDescending(r => r.RequestedAt)
            .Take(30)
            .ToListAsync(cancellationToken);
        HashSet<int> releasing = approvals.Where(r => r is { Kind: ApprovalKind.ReleaseHalt, Status: ApprovalStatus.Pending, TradingHaltId: not null }).Select(r => r.TradingHaltId!.Value).ToHashSet();

        // One mark per venue and instrument, read once.
        Dictionary<(Venue, string, InstrumentType, string?), decimal?> marks = [];
        foreach (var held in positions.Select(p => { TradingAccount owner = accounts.First(a => a.Id == p.TradingAccountId); return (owner.Venue, p.Asset, p.InstrumentType, Quote: owner.QuoteFor(p.InstrumentType)); }).Distinct())
        {
            marks[held] = ConsolidatedBook.Pick(await books.ReadAsync(held.Asset, held.InstrumentType, cancellationToken), held.Venue, held.Quote)?.Mid;
        }

        List<TradingAccountDto> rows = accounts
            .Select(a =>
            {
                List<TradingPositionDto> own = positions
                    .Where(p => p.TradingAccountId == a.Id)
                    .OrderBy(p => p.Asset)
                    .Select(p =>
                    {
                        decimal? mark = marks.GetValueOrDefault((a.Venue, p.Asset, p.InstrumentType, a.QuoteFor(p.InstrumentType)));
                        decimal? distance = mark is decimal at && at > 0m && p.LiquidationPrice is decimal liquidation && p.Quantity != 0m ? Math.Round(Math.Abs(at - liquidation) / at, 4) : null;
                        return new TradingPositionDto(p.Asset, p.InstrumentType, p.Quantity, p.AveragePrice, mark, mark * Math.Abs(p.Quantity), mark is decimal m && p.AveragePrice > 0m ? Math.Round(p.Quantity * (m - p.AveragePrice), 2) : null, p.RealisedPnlUsd, p.FeesUsd, p.Quantity == 0m ? null : p.LiquidationPrice, distance);
                    })
                    .ToList();
                // A real account is worth what the venue says it is worth; a paper one, its starting balance and what its trades made.
                decimal equity = a.Mode == TradingMode.Live ? a.LiveEquityUsd ?? 0m : a.PaperBalanceUsd + own.Sum(p => p.RealisedPnlUsd - p.FeesUsd + (p.UnrealisedPnlUsd ?? 0m));
                return new TradingAccountDto(
                    a.Id,
                    a.ManagerAccountId,
                    a.ManagerAccount.Name,
                    a.ManagerAccount.Sleeve.Name,
                    a.Venue,
                    VenueExposureService.NameOf(a.Venue),
                    a.Label,
                    a.Mode,
                    a.EncryptedKey is not null,
                    a.KeyHint,
                    a.Leverage,
                    a.MarginMode,
                    a.MaxOrderNotionalUsd,
                    a.MaxPositionNotionalUsd,
                    a.PriceBandBps,
                    a.PaperBalanceUsd,
                    Math.Round(equity, 2),
                    Covers(halts, a),
                    // A real account lists everything the venue holds for it; what is at nothing is left out.
                    a.Mode == TradingMode.Live ? own.Where(p => p.Quantity != 0m).ToList() : own,
                    a.Mode == TradingMode.Paper && a.EncryptedKey is not null && LiveVenues.Contains(a.Venue) && a.PerpQuote == "USDT",
                    a.Mode == TradingMode.Live ? a.LiveSyncedAt : null,
                    a.Mode == TradingMode.Live ? a.LiveSyncError : null,
                    a.PerpQuote
                );
            })
            .ToList();

        List<ManagerOption> managers = await database
            .ManagerAccounts.AsNoTracking()
            .Where(m => m.Sleeve.FundId == fundId && m.IsActive)
            .OrderBy(m => m.SleeveId)
            .ThenBy(m => m.Id)
            .Select(m => new ManagerOption(m.Id, m.Name, m.Sleeve.Name))
            .ToListAsync(cancellationToken);
        IReadOnlyList<LiveInstrument> live = await books.InstrumentsAsync(cancellationToken);
        return new TradingDto(
            fundId,
            fund.Name,
            fund.IsDemo,
            actor.CanTrade && !fund.IsDemo,
            actor.IsAdmin && !fund.IsDemo,
            await vault.CanSealAsync(cancellationToken),
            rows,
            halts.Where(h => h.Scope != HaltScope.Account || accountIds.Contains(h.TradingAccountId ?? 0)).Select(h => Dto(h, accounts, releasing.Contains(h.Id))).ToList(),
            approvals.Select(r => Dto(r, actor)).ToList(),
            Venues.Select(v => new VenueOption(v, VenueExposureService.NameOf(v), adapters.Find(v)?.NeedsPassphrase ?? false)).ToList(),
            managers,
            live.Where(i => i.Type is InstrumentType.Perp or InstrumentType.Spot).ToList(),
            await books.StatusAsync(cancellationToken),
            (await database.PinnedInstruments.AsNoTracking().Where(p => p.FundId == fundId).OrderBy(p => p.Id).Select(p => new { p.Asset, p.Type }).ToListAsync(cancellationToken)).Select(p => new InstrumentRef(p.Asset, p.Type)).ToList(),
            await RecentAsync(accountIds, cancellationToken),
            fund.IncludePaperTrading
        );
    }

    /// <summary>
    /// Instruments whose asset starts with what was typed, across every venue's list: the exact name first, then
    /// the ones most venues have.
    /// </summary>
    public async Task<IReadOnlyList<InstrumentHit>> SearchInstrumentsAsync(string? text, CancellationToken cancellationToken)
    {
        string typed = new((text ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit).Take(20).ToArray());
        if (typed.Length == 0)
        {
            return [];
        }

        var rows = await database
            .ListedInstruments.AsNoTracking()
            .Where(i => i.Tradable && i.Asset.StartsWith(typed) && Venues.Contains(i.Venue))
            .Select(i => new { i.Asset, i.Type, i.Venue })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => (r.Asset, r.Type))
            .Select(g => new InstrumentHit(g.Key.Asset, g.Key.Type, g.Select(r => r.Venue).Distinct().OrderBy(v => v).ToList()))
            .OrderByDescending(h => h.Asset == typed)
            .ThenByDescending(h => h.Venues.Count)
            .ThenBy(h => h.Asset, StringComparer.Ordinal)
            .ThenBy(h => h.Type)
            .Take(40)
            .ToList();
    }

    /// <summary>The instruments last traded on these accounts, the latest first.</summary>
    private async Task<IReadOnlyList<InstrumentRef>> RecentAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken)
    {
        var latest = await database
            .Orders.AsNoTracking()
            .Where(o => accountIds.Contains(o.TradingAccountId))
            .GroupBy(o => new { o.Asset, o.InstrumentType })
            .Select(g => new { g.Key.Asset, g.Key.InstrumentType, Last = g.Max(o => o.Id) })
            .OrderByDescending(g => g.Last)
            .Take(6)
            .ToListAsync(cancellationToken);
        return latest.Select(l => new InstrumentRef(l.Asset, l.InstrumentType)).ToList();
    }

    /// <summary>
    /// Says whether what the fund's paper accounts hold counts in its holdings, exposure, risk and NAV. Turning it
    /// on or off moves the accounts' value into or out of the book as money, not as a gain or a loss.
    /// </summary>
    public async Task<TradingChange<int>> SetPaperInBookAsync(int fundId, bool include, Actor actor, CancellationToken cancellationToken)
    {
        if (!actor.IsAdmin)
        {
            return TradingChange<int>.Refused("Only an administrator can change what counts in the fund's book.");
        }

        Fund? fund = await database.Funds.FirstOrDefaultAsync(f => f.Id == fundId, cancellationToken);
        if (fund is null)
        {
            return TradingChange<int>.Missing;
        }

        if (fund.IsDemo)
        {
            return TradingChange<int>.Refused(DemoIsFixed);
        }

        fund.IncludePaperTrading = include;
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(fundId, include ? "Paper accounts now count in this fund's book. Their value comes in as money arriving, within half a minute." : "Paper accounts no longer count in this fund's book. Their value leaves as money leaving, within half a minute.");
    }

    /// <summary>Keeps an instrument at hand on a fund's trading page, with its book always open.</summary>
    public async Task<TradingChange<int>> PinAsync(int fundId, InstrumentRef instrument, Actor actor, CancellationToken cancellationToken)
    {
        if (!actor.CanTrade)
        {
            return TradingChange<int>.Refused("Only a trader or an administrator can pin an instrument.");
        }

        if (!await database.Funds.AnyAsync(f => f.Id == fundId, cancellationToken))
        {
            return TradingChange<int>.Missing;
        }

        string asset = (instrument.Asset ?? string.Empty).Trim().ToUpperInvariant();
        if (instrument.Type is not (InstrumentType.Spot or InstrumentType.Perp) || !await database.ListedInstruments.AnyAsync(i => i.Asset == asset && i.Type == instrument.Type, cancellationToken))
        {
            return TradingChange<int>.Refused("No venue lists that instrument.");
        }

        List<PinnedInstrument> pins = await database.PinnedInstruments.Where(p => p.FundId == fundId).ToListAsync(cancellationToken);
        if (pins.FirstOrDefault(p => p.Asset == asset && p.Type == instrument.Type) is { } already)
        {
            return TradingChange<int>.Ok(already.Id);
        }

        if (pins.Count >= MostPins)
        {
            return TradingChange<int>.Refused($"A fund can pin {MostPins} instruments. Unpin one first.");
        }

        PinnedInstrument pin = new() { FundId = fundId, Asset = asset, Type = instrument.Type };
        database.PinnedInstruments.Add(pin);
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(pin.Id);
    }

    public async Task<TradingChange<int>> UnpinAsync(int fundId, InstrumentRef instrument, Actor actor, CancellationToken cancellationToken)
    {
        if (!actor.CanTrade)
        {
            return TradingChange<int>.Refused("Only a trader or an administrator can unpin an instrument.");
        }

        string asset = (instrument.Asset ?? string.Empty).Trim().ToUpperInvariant();
        PinnedInstrument? pin = await database.PinnedInstruments.FirstOrDefaultAsync(p => p.FundId == fundId && p.Asset == asset && p.Type == instrument.Type, cancellationToken);
        if (pin is null)
        {
            return TradingChange<int>.Missing;
        }

        database.PinnedInstruments.Remove(pin);
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(pin.Id);
    }

    /// <summary>What each venue's list says about one instrument.</summary>
    public async Task<IReadOnlyList<VenueRules>> RulesAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        string name = asset.ToUpperInvariant();
        var rows = await database.ListedInstruments.AsNoTracking().Where(i => i.Asset == name && i.Type == type && Venues.Contains(i.Venue)).OrderBy(i => i.Venue).ToListAsync(cancellationToken);
        return rows.Select(i => new VenueRules(i.Venue, i.Quote, i.Tradable, i.PriceStep, i.QuantityStep, i.MinQuantity)).ToList();
    }

    public async Task<BookDto> BookAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        IReadOnlyList<VenueBook> venues = await books.ReadAsync(asset, type, cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        // The best prices are taken among venues that quote in the same thing. A dollar and a USDT are not quite the
        // same money, and a venue must not look best only because it counts in the dearer one.
        List<VenueBook> alike = venues.Where(v => v.Quote == "USDT").ToList();
        if (alike.Count == 0)
        {
            alike = venues.Where(v => v.Quote == venues[0].Quote).ToList();
        }

        VenueBook? bid = alike.Where(v => v.BestBid is not null).OrderByDescending(v => v.BestBid).FirstOrDefault();
        VenueBook? ask = alike.Where(v => v.BestAsk is not null).OrderBy(v => v.BestAsk).FirstOrDefault();
        decimal? mid = ConsolidatedBook.Mid(alike);
        return new BookDto(
            asset.ToUpperInvariant(),
            type,
            bid?.BestBid,
            bid?.Venue,
            ask?.BestAsk,
            ask?.Venue,
            mid,
            bid?.BestBid is decimal b && ask?.BestAsk is decimal a && mid is > 0m ? Math.Round((a - b) / mid.Value * 10_000m, 2) : null,
            venues.Select(v => new BookVenueDto(v.Venue, VenueExposureService.NameOf(v.Venue), v.BestBid, v.BestAsk, v.Bids.Count > 0 ? v.Bids[0].Quantity : null, v.Asks.Count > 0 ? v.Asks[0].Quantity : null, (now - v.At).TotalMilliseconds, v.Bids.Take(10).ToList(), v.Asks.Take(10).ToList(), v.Quote)).ToList()
        );
    }

    public async Task<IReadOnlyList<OrderDto>?> OrdersAsync(int fundId, bool openOnly, CancellationToken cancellationToken)
    {
        if (!await database.Funds.AnyAsync(f => f.Id == fundId, cancellationToken))
        {
            return null;
        }

        List<Order> orders = await database
            .Orders.AsNoTracking()
            .Include(o => o.TradingAccount)
            .ThenInclude(a => a.ManagerAccount)
            .Include(o => o.Events)
            .Where(o => o.TradingAccount.ManagerAccount.Sleeve.FundId == fundId)
            .Where(o => !openOnly || o.Status == OrderStatus.Received || o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Working || o.Status == OrderStatus.PartlyFilled)
            .OrderByDescending(o => o.Id)
            .Take(MaxOrdersListed)
            .ToListAsync(cancellationToken);
        return orders.Select(Dto).ToList();
    }

    public async Task<OrderDto?> OrderAsync(long orderId, CancellationToken cancellationToken)
    {
        Order? order = await database.Orders.AsNoTracking().Include(o => o.TradingAccount).ThenInclude(a => a.ManagerAccount).Include(o => o.Events).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        return order is null ? null : Dto(order);
    }

    // ---------------------------------------------------------------- accounts, keys and limits

    public async Task<TradingChange<int>> AddAccountAsync(SaveTradingAccountRequest request, Actor actor, CancellationToken cancellationToken)
    {
        ManagerAccount? manager = await database.ManagerAccounts.Include(m => m.Sleeve).ThenInclude(s => s.Fund).FirstOrDefaultAsync(m => m.Id == request.ManagerId, cancellationToken);
        if (manager is null)
        {
            return TradingChange<int>.Missing;
        }

        if (Limits(request) is { } invalid)
        {
            return TradingChange<int>.Refused(invalid);
        }

        if (manager.Sleeve.Fund.IsDemo)
        {
            return TradingChange<int>.Refused(DemoIsFixed);
        }

        if (!manager.IsActive)
        {
            return TradingChange<int>.Refused(ConnectionService.RetiredManager);
        }

        if (!Venues.Contains(request.Venue))
        {
            return TradingChange<int>.Refused($"Orders cannot be sent to {VenueExposureService.NameOf(request.Venue)} yet.");
        }

        string perpQuote = string.IsNullOrWhiteSpace(request.PerpQuote) ? "USDT" : request.PerpQuote.Trim().ToUpperInvariant();
        if (perpQuote != "USDT" && !(perpQuote == "USDC" && UsdcPerpVenues.Contains(request.Venue)))
        {
            return TradingChange<int>.Refused($"Perpetuals on {VenueExposureService.NameOf(request.Venue)} are margined in USDT here.");
        }

        // One account for each family of perpetuals a venue has: a manager can have a USDT and a USDC account on the same venue.
        if (await database.TradingAccounts.AnyAsync(a => a.ManagerAccountId == request.ManagerId && a.Venue == request.Venue && a.PerpQuote == perpQuote, cancellationToken))
        {
            return TradingChange<int>.Refused($"{manager.Name} already has a trading account on {VenueExposureService.NameOf(request.Venue)}{(perpQuote == "USDT" ? string.Empty : " for " + perpQuote + " perpetuals")}.");
        }

        TradingAccount account = new()
        {
            ManagerAccountId = manager.Id,
            Venue = request.Venue,
            Label = request.Label.Trim(),
            Mode = TradingMode.Paper,
            Leverage = request.Leverage,
            MarginMode = request.MarginMode,
            MaxOrderNotionalUsd = request.MaxOrderNotionalUsd,
            MaxPositionNotionalUsd = request.MaxPositionNotionalUsd,
            PriceBandBps = request.PriceBandBps,
            PaperBalanceUsd = request.PaperBalanceUsd,
            PerpQuote = perpQuote,
            CreatedBy = actor.Name,
            CreatedAt = clock.GetUtcNow(),
        };
        database.TradingAccounts.Add(account);
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(account.Id);
    }

    /// <summary>Tightening a limit takes effect at once; loosening one waits for a second person.</summary>
    public async Task<TradingChange<int>> SaveLimitsAsync(int accountId, SaveTradingAccountRequest request, Actor actor, CancellationToken cancellationToken)
    {
        TradingAccount? account = await database.TradingAccounts.Include(a => a.ManagerAccount).ThenInclude(m => m.Sleeve).FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return TradingChange<int>.Missing;
        }

        if (Limits(request) is { } invalid)
        {
            return TradingChange<int>.Refused(invalid);
        }

        account.Label = request.Label.Trim();
        // Cross margin puts the whole account behind every position, so going to it is a loosening like a higher limit.
        bool toCross = request.MarginMode == MarginMode.Cross && account.MarginMode != MarginMode.Cross;
        if (!toCross)
        {
            account.MarginMode = request.MarginMode;
        }

        bool looser = toCross || request.Leverage > account.Leverage || request.MaxOrderNotionalUsd > account.MaxOrderNotionalUsd || request.MaxPositionNotionalUsd > account.MaxPositionNotionalUsd || request.PriceBandBps > account.PriceBandBps;
        // Whatever gets tighter is applied now, also when the rest has to wait.
        account.Leverage = Math.Min(account.Leverage, request.Leverage);
        account.MaxOrderNotionalUsd = Math.Min(account.MaxOrderNotionalUsd, request.MaxOrderNotionalUsd);
        account.MaxPositionNotionalUsd = Math.Min(account.MaxPositionNotionalUsd, request.MaxPositionNotionalUsd);
        account.PriceBandBps = Math.Min(account.PriceBandBps, request.PriceBandBps);
        string? notice = null;
        if (looser)
        {
            string summary =
                $"Raise limits of {account.ManagerAccount.Name} on {VenueExposureService.NameOf(account.Venue)}: leverage {request.Leverage:0.##}×, largest order {request.MaxOrderNotionalUsd:N0} USD, largest position {request.MaxPositionNotionalUsd:N0} USD, price band {request.PriceBandBps / 100m:0.##}%{(toCross ? ", cross margin" : string.Empty)}";
            Ask(ApprovalKind.RaiseLimits, account.ManagerAccount.Sleeve.FundId, account.Id, null, summary, new { request.Leverage, request.MaxOrderNotionalUsd, request.MaxPositionNotionalUsd, request.PriceBandBps, ToCross = toCross }, actor);
            notice = "Limits that got tighter apply now. The higher ones wait for a second person's approval.";
        }

        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(account.Id, notice);
    }

    /// <summary>
    /// Takes a trading key for an account. The key is checked with the venue first: it must be able to trade and must
    /// not be able to withdraw. It is then sealed for the execution service and waits for a second person's approval.
    /// </summary>
    public async Task<TradingChange<int>> RequestKeyAsync(int accountId, TradingKeyRequest request, Actor actor, CancellationToken cancellationToken)
    {
        TradingAccount? account = await database.TradingAccounts.Include(a => a.ManagerAccount).ThenInclude(m => m.Sleeve).FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return TradingChange<int>.Missing;
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey) || string.IsNullOrWhiteSpace(request.ApiSecret) || request.ApiKey.Length > 300 || request.ApiSecret.Length > 600)
        {
            return TradingChange<int>.Refused("Give the key and its secret.");
        }

        if (!await vault.CanSealAsync(cancellationToken))
        {
            return TradingChange<int>.Refused("The execution service has not started yet, so there is nowhere safe to keep a trading key. Try again once it is running.");
        }

        string venueName = VenueExposureService.NameOf(account.Venue);
        VenueCredentials credentials = new(request.ApiKey.Trim(), request.ApiSecret.Trim(), string.IsNullOrWhiteSpace(request.Passphrase) ? null : request.Passphrase.Trim());
        string checkedHow;
        string? uncheckedNotice = null;
        if (account.Venue == Venue.Hyperliquid)
        {
            // Hyperliquid trades through an "API wallet": a second key that can place orders for the account and, by the venue's design, cannot move funds out.
            if (!IsHex(credentials.ApiKey, 40) || !IsHex(credentials.ApiSecret, 64))
            {
                return TradingChange<int>.Refused("For Hyperliquid give the account's address (0x and 40 characters) as the key and its API wallet's private key (0x and 64 characters) as the secret. Never the main wallet's own private key.");
            }

            checkedHow = "format checked; Hyperliquid API wallets cannot withdraw by design, and the link to the account is confirmed when it first trades";
        }
        else
        {
            IVenueAdapter? adapter = adapters.Find(account.Venue);
            if (adapter is null)
            {
                return TradingChange<int>.Refused($"{venueName} is not supported.");
            }

            if (adapter.NeedsPassphrase && credentials.Passphrase is null)
            {
                return TradingChange<int>.Refused($"{venueName} keys come with a passphrase.");
            }

            CredentialCheck check;
            try
            {
                check = await adapter.CheckCredentialsAsync(credentials, cancellationToken);
            }
            catch (VenueException e)
            {
                return TradingChange<int>.Refused(e.Failure == VenueFailure.Rejected ? $"{venueName} refused the key: {e.Message}" : $"{venueName} could not be reached: {e.Message}");
            }

            if (check.CanWithdraw == true)
            {
                return TradingChange<int>.Refused($"This key can withdraw. Create a key on {venueName} that can trade but cannot withdraw, and use that.");
            }

            if (check.CanWithdraw is null || check.CanTrade is null)
            {
                if (!UncheckableVenues.Contains(account.Venue))
                {
                    return TradingChange<int>.Refused($"{venueName} did not say what this key may do, so it cannot be confirmed that it cannot withdraw. It is not accepted.");
                }

                // These venues have no way to ask what a key may do. The key works, and that is all that is known:
                // whether it can withdraw has to be looked at on the venue itself, by the person who approves.
                checkedHow = $"NOT CHECKED: {venueName} cannot say what a key may do. Before approving, look at the key on {venueName} and make sure withdrawal is switched off";
                uncheckedNotice = $"The key works, but {venueName} cannot say what it may do. Whoever approves must first look at the key on {venueName} and make sure it cannot withdraw.";
            }
            else if (check.CanTrade != true)
            {
                return TradingChange<int>.Refused($"This key cannot trade. Create a key on {venueName} with trading switched on and withdrawals off.");
            }
            else
            {
                checkedHow = $"{venueName} confirms it can trade and cannot withdraw";
            }
        }

        string hint = credentials.ApiKey.Length <= 4 ? credentials.ApiKey : credentials.ApiKey[^4..];
        string sealedKey = await vault.SealAsync(credentials, cancellationToken);
        ApprovalRequest asked = Ask(ApprovalKind.AddTradingKey, account.ManagerAccount.Sleeve.FundId, account.Id, null, $"Store trading key …{hint} for {account.ManagerAccount.Name} on {venueName} ({checkedHow})", new { SealedKey = sealedKey, KeyHint = hint }, actor);
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(asked.Id, uncheckedNotice ?? "The key passed its check and is sealed. It is stored on the account once a second person approves.");
    }

    // ---------------------------------------------------------------- kill switches

    /// <summary>Pulls a kill switch. One person is enough, and it works at once.</summary>
    public async Task<TradingChange<int>> PullHaltAsync(int fundId, PullHaltRequest request, Actor actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
        {
            return TradingChange<int>.Refused("Say why, in up to 500 characters.");
        }

        TradingHalt halt = new() { Scope = request.Scope, Reason = request.Reason.Trim(), PulledBy = actor.Name, PulledAt = clock.GetUtcNow() };
        switch (request.Scope)
        {
            case HaltScope.Venue when request.Venue is Venue venue && Venues.Contains(venue):
                halt.Venue = venue;
                break;
            case HaltScope.Account when request.TradingAccountId is int accountId:
                if (!await database.TradingAccounts.AnyAsync(a => a.Id == accountId && a.ManagerAccount.Sleeve.FundId == fundId, cancellationToken))
                {
                    return TradingChange<int>.Missing;
                }

                halt.TradingAccountId = accountId;
                break;
            case HaltScope.Global:
                break;
            default:
                return TradingChange<int>.Refused("Say which venue or which account.");
        }

        if (await database.TradingHalts.AnyAsync(h => h.ReleasedAt == null && h.Scope == halt.Scope && h.Venue == halt.Venue && h.TradingAccountId == halt.TradingAccountId, cancellationToken))
        {
            return TradingChange<int>.Refused("That kill switch is already pulled.");
        }

        database.TradingHalts.Add(halt);
        await database.SaveChangesAsync(cancellationToken);
        // The execution service cancels what is open in the halt's scope on its next round, which this brings forward.
        await signal.WakeAsync(cancellationToken);
        return TradingChange<int>.Ok(halt.Id);
    }

    /// <summary>Asks for a kill switch to be released. A second person has to agree.</summary>
    public async Task<TradingChange<int>> RequestReleaseAsync(int haltId, Actor actor, CancellationToken cancellationToken)
    {
        TradingHalt? halt = await database.TradingHalts.FirstOrDefaultAsync(h => h.Id == haltId && h.ReleasedAt == null, cancellationToken);
        if (halt is null)
        {
            return TradingChange<int>.Missing;
        }

        if (await database.ApprovalRequests.AnyAsync(r => r.TradingHaltId == haltId && r.Status == ApprovalStatus.Pending, cancellationToken))
        {
            return TradingChange<int>.Refused("Its release has already been asked for and is waiting for approval.");
        }

        int? fundId = halt.TradingAccountId is int accountId ? await database.TradingAccounts.Where(a => a.Id == accountId).Select(a => (int?)a.ManagerAccount.Sleeve.FundId).FirstOrDefaultAsync(cancellationToken) : null;
        ApprovalRequest asked = Ask(ApprovalKind.ReleaseHalt, fundId, halt.TradingAccountId, halt.Id, $"Release the kill switch on {await WhereAsync(halt, cancellationToken)} (pulled by {halt.PulledBy}: {halt.Reason})", new { HaltId = halt.Id }, actor);
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(asked.Id, "The release waits for a second person's approval.");
    }

    // ---------------------------------------------------------------- approvals

    /// <summary>Approves or rejects a request. Never by the person who made it; that is the whole point.</summary>
    public async Task<TradingChange<int>> DecideAsync(int requestId, bool approve, string? note, Actor actor, CancellationToken cancellationToken)
    {
        ApprovalRequest? request = await database.ApprovalRequests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
        if (request is null)
        {
            return TradingChange<int>.Missing;
        }

        if (request.Status != ApprovalStatus.Pending)
        {
            return TradingChange<int>.Refused("That request has already been decided.");
        }

        if (string.Equals(request.RequestedBy, actor.Name, StringComparison.OrdinalIgnoreCase))
        {
            return TradingChange<int>.Refused("A request has to be approved by someone other than the person who made it.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        request.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        request.DecidedBy = actor.Name;
        request.DecidedAt = now;
        // The column holds a thousand characters; a longer note is cut, not allowed to fail the decision.
        request.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() is { Length: > 1000 } longNote ? longNote[..1000] : note.Trim();
        if (approve)
        {
            using JsonDocument payload = JsonDocument.Parse(request.PayloadJson);
            JsonElement p = payload.RootElement;
            TradingAccount? account = request.TradingAccountId is int accountId ? await database.TradingAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken) : null;
            switch (request.Kind)
            {
                case ApprovalKind.AddTradingKey when account is not null:
                    account.EncryptedKey = p.GetProperty("sealedKey").GetString();
                    account.KeyHint = p.GetProperty("keyHint").GetString();
                    break;
                case ApprovalKind.RaiseLimits when account is not null:
                    account.Leverage = p.GetProperty("leverage").GetDecimal();
                    account.MaxOrderNotionalUsd = p.GetProperty("maxOrderNotionalUsd").GetDecimal();
                    account.MaxPositionNotionalUsd = p.GetProperty("maxPositionNotionalUsd").GetDecimal();
                    account.PriceBandBps = p.GetProperty("priceBandBps").GetInt32();
                    if (p.TryGetProperty("toCross", out JsonElement cross) && cross.ValueKind == JsonValueKind.True)
                    {
                        account.MarginMode = MarginMode.Cross;
                    }

                    break;
                case ApprovalKind.GoLive when account is not null:
                    if (account.EncryptedKey is null)
                    {
                        return TradingChange<int>.Refused("The account no longer has a trading key.");
                    }

                    if (await HasOpenOrdersAsync(account.Id, cancellationToken))
                    {
                        return TradingChange<int>.Refused("The account has open paper orders. Cancel them first; real trading starts from a clean sheet.");
                    }

                    // Paper positions were never real. From here the account shows what the venue holds for it.
                    database.TradingPositions.RemoveRange(await database.TradingPositions.Where(p => p.TradingAccountId == account.Id).ToListAsync(cancellationToken));
                    account.Mode = TradingMode.Live;
                    account.LiveEquityUsd = null;
                    account.LiveSyncedAt = null;
                    account.LiveSyncError = null;
                    break;
                case ApprovalKind.LargeOrder:
                    if (!await WorkingOrderService.DecideAsync(database, request, true, actor.Name, now, cancellationToken))
                    {
                        return TradingChange<int>.Refused("That working order is no longer waiting for approval.");
                    }

                    break;
                case ApprovalKind.ReleaseHalt:
                    TradingHalt? halt = await database.TradingHalts.FirstOrDefaultAsync(h => h.Id == request.TradingHaltId && h.ReleasedAt == null, cancellationToken);
                    if (halt is not null)
                    {
                        halt.ReleasedAt = now;
                        halt.ReleasedBy = $"{request.RequestedBy}, approved by {actor.Name}";
                    }

                    break;
                default:
                    return TradingChange<int>.Refused("What this request is about no longer exists.");
            }
        }

        if (!approve && request.Kind == ApprovalKind.LargeOrder)
        {
            await WorkingOrderService.DecideAsync(database, request, false, actor.Name, now, cancellationToken);
        }

        // A sealed key that was not approved is not kept.
        if (request.Kind == ApprovalKind.AddTradingKey)
        {
            request.PayloadJson = "{}";
        }

        await database.SaveChangesAsync(cancellationToken);
        // An order sent on paper at the very moment the account went live must not go to a venue. The account is
        // looked at again now that it is live; if such an order is there, the switch is undone and has to be asked for again.
        if (approve && request.Kind == ApprovalKind.GoLive && request.TradingAccountId is int wentLive && await HasOpenOrdersAsync(wentLive, cancellationToken))
        {
            TradingAccount? undone = await database.TradingAccounts.FirstOrDefaultAsync(a => a.Id == wentLive, cancellationToken);
            if (undone is not null)
            {
                undone.Mode = TradingMode.Paper;
                request.Status = ApprovalStatus.Rejected;
                request.Note = "An order came in at the same moment, so the account stayed on paper.";
                await database.SaveChangesAsync(cancellationToken);
                return TradingChange<int>.Refused("An order was sent on this account at the same moment, so it stayed on paper. Cancel the order and ask again.");
            }
        }

        return TradingChange<int>.Ok(request.Id);
    }

    /// <summary>
    /// Asks for an account to trade real money. It needs a trading key that has passed its check, a venue real
    /// orders can be sent to, and no open paper orders; and then a second administrator.
    /// </summary>
    public async Task<TradingChange<int>> RequestGoLiveAsync(int accountId, Actor actor, CancellationToken cancellationToken)
    {
        TradingAccount? account = await database.TradingAccounts.Include(a => a.ManagerAccount).ThenInclude(m => m.Sleeve).FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return TradingChange<int>.Missing;
        }

        string venue = VenueExposureService.NameOf(account.Venue);
        string? refusal =
            account.Mode == TradingMode.Live ? "The account already trades real money."
            : !LiveVenues.Contains(account.Venue) ? $"Real orders cannot be sent to {venue} yet. It trades on paper."
            : account.PerpQuote != "USDT" ? $"Real orders in {account.PerpQuote}-margined perpetuals are not connected yet. This account trades on paper."
            : account.EncryptedKey is null ? "Add a trading key first, and have it approved."
            : await HasOpenOrdersAsync(accountId, cancellationToken) ? "The account has open paper orders. Cancel them first; real trading starts from a clean sheet."
            : await database.ApprovalRequests.AnyAsync(r => r.TradingAccountId == accountId && r.Kind == ApprovalKind.GoLive && r.Status == ApprovalStatus.Pending, cancellationToken) ? "This has already been asked for and is waiting for a second administrator."
            : null;
        if (refusal is not null)
        {
            return TradingChange<int>.Refused(refusal);
        }

        Ask(
            ApprovalKind.GoLive,
            account.ManagerAccount.Sleeve.FundId,
            account.Id,
            null,
            $"Trade real money on {account.ManagerAccount.Name} · {venue}: orders up to {account.MaxOrderNotionalUsd:N0} USD, positions up to {account.MaxPositionNotionalUsd:N0} USD, leverage {account.Leverage:0.##}×",
            new { },
            actor
        );
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(account.Id, "Asked for. Real trading starts when a second administrator approves.");
    }

    /// <summary>
    /// Takes an account back to paper. One administrator is enough: it is the safe direction. Whatever the account
    /// holds at the venue stays there, untouched; it is simply no longer traded or shown from here.
    /// </summary>
    public async Task<TradingChange<int>> BackToPaperAsync(int accountId, CancellationToken cancellationToken)
    {
        TradingAccount? account = await database.TradingAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return TradingChange<int>.Missing;
        }

        if (account.Mode != TradingMode.Live)
        {
            return TradingChange<int>.Refused("The account already trades on paper.");
        }

        if (await HasOpenOrdersAsync(accountId, cancellationToken))
        {
            return TradingChange<int>.Refused("The account has open orders at the venue. Cancel them first, or pull the kill switch, and wait until they are gone.");
        }

        // The switch is made first and looked at again afterwards: an order that came in at the same moment was taken
        // as a real one, and must not be left to the paper engine. Then the switch is undone and refused.
        account.Mode = TradingMode.Paper;
        await database.SaveChangesAsync(cancellationToken);
        if (await HasOpenOrdersAsync(accountId, cancellationToken))
        {
            account.Mode = TradingMode.Live;
            await database.SaveChangesAsync(cancellationToken);
            return TradingChange<int>.Refused("An order came in at the same moment. Cancel it first, or pull the kill switch, and try again once it is gone.");
        }

        database.TradingPositions.RemoveRange(await database.TradingPositions.Where(p => p.TradingAccountId == accountId).ToListAsync(cancellationToken));
        account.LiveEquityUsd = null;
        account.LiveSyncedAt = null;
        account.LiveSyncError = null;
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(account.Id, "Back on paper. What the account holds at the venue has not been touched.");
    }

    private Task<bool> HasOpenOrdersAsync(int accountId, CancellationToken cancellationToken) =>
        database.Orders.AnyAsync(o => o.TradingAccountId == accountId && (o.Status == OrderStatus.Received || o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Working || o.Status == OrderStatus.PartlyFilled), cancellationToken);

    /// <summary>Withdraws one's own request.</summary>
    public async Task<TradingChange<int>> CancelRequestAsync(int requestId, Actor actor, CancellationToken cancellationToken)
    {
        ApprovalRequest? request = await database.ApprovalRequests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
        if (request is null)
        {
            return TradingChange<int>.Missing;
        }

        if (request.Status != ApprovalStatus.Pending || !string.Equals(request.RequestedBy, actor.Name, StringComparison.OrdinalIgnoreCase))
        {
            return TradingChange<int>.Refused("Only a request that is still waiting can be withdrawn, and only by whoever made it.");
        }

        request.Status = ApprovalStatus.Cancelled;
        request.DecidedBy = actor.Name;
        request.DecidedAt = clock.GetUtcNow();
        request.PayloadJson = request.Kind == ApprovalKind.AddTradingKey ? "{}" : request.PayloadJson;
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(request.Id);
    }

    // ---------------------------------------------------------------- orders

    /// <summary>
    /// Takes an order in: validates its shape, writes it down as received and wakes the execution service. Every
    /// check on limits, position and market is made there, by the one process that also acts on the order.
    /// </summary>
    /// <param name="strategy">The strategy key the order came with; null for a person's ticket.</param>
    public async Task<TradingChange<OrderDto>> SubmitAsync(SubmitOrderRequest request, Actor actor, StrategyKey? strategy, CancellationToken cancellationToken)
    {
        string asset = (request.Asset ?? string.Empty).Trim().ToUpperInvariant();
        if (asset.Length is 0 or > 32 || !asset.All(char.IsLetterOrDigit))
        {
            return TradingChange<OrderDto>.Refused("Name the asset, for example BTC.");
        }

        if (request.InstrumentType is not (InstrumentType.Perp or InstrumentType.Spot))
        {
            return TradingChange<OrderDto>.Refused("Orders are for perpetuals or spot.");
        }

        if (!Enum.IsDefined(request.Side) || !Enum.IsDefined(request.Type) || !Enum.IsDefined(request.TimeInForce))
        {
            return TradingChange<OrderDto>.Refused("The side, the order type or how long it stays is not one this system knows.");
        }

        if (request.Quantity <= 0m || request.Quantity > 1_000_000_000m || request.LimitPrice is <= 0m or > 1_000_000_000m)
        {
            return TradingChange<OrderDto>.Refused("The quantity and the price must be above zero and within reason.");
        }

        string clientOrderId = string.IsNullOrWhiteSpace(request.ClientOrderId) ? Guid.NewGuid().ToString("N") : request.ClientOrderId.Trim();
        if (clientOrderId.Length > 64 || !clientOrderId.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return TradingChange<OrderDto>.Refused("The order's own name may be up to 64 letters, digits, dashes, dots and underscores.");
        }

        TradingAccount? account = await PickAccountAsync(request, asset, cancellationToken);
        if (account is null)
        {
            return request.TradingAccountId is null && request.ManagerId is null ? TradingChange<OrderDto>.Refused("Say which trading account, or which manager.") : TradingChange<OrderDto>.Missing;
        }

        if (account.ManagerAccount.Sleeve.Fund.IsDemo)
        {
            return TradingChange<OrderDto>.Refused(DemoIsFixed);
        }

        if (!account.ManagerAccount.IsActive)
        {
            return TradingChange<OrderDto>.Refused(ConnectionService.RetiredManager);
        }

        if (strategy is not null && !AccountIds(strategy).Contains(account.Id))
        {
            return TradingChange<OrderDto>.Refused("This strategy key is not allowed to trade that account.");
        }

        // The same name on the same account is the same order: a retry after a lost answer must not trade twice. An
        // order sent to a manager rather than to one account is looked for on all of that manager's accounts, because
        // the retry may be routed to a different venue than the first attempt was.
        int managerId = account.ManagerAccountId;
        bool anyOfTheManagers = request.TradingAccountId is null;
        Order? existing = await database.Orders.AsNoTracking().Include(o => o.TradingAccount).ThenInclude(a => a.ManagerAccount).Include(o => o.Events)
            .FirstOrDefaultAsync(o => o.ClientOrderId == clientOrderId && (o.TradingAccountId == account.Id || (anyOfTheManagers && o.TradingAccount.ManagerAccountId == managerId)), cancellationToken);
        if (existing is not null)
        {
            return TradingChange<OrderDto>.Ok(Dto(existing), "An order with this name already exists; it is returned unchanged.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        Order order = new()
        {
            ClientOrderId = clientOrderId,
            TradingAccountId = account.Id,
            TradingAccount = account,
            Asset = asset,
            InstrumentType = request.InstrumentType,
            Side = request.Side,
            Type = request.Type,
            // A market order never rests.
            TimeInForce = request.Type == OrderType.Market ? TimeInForce.ImmediateOrCancel : request.TimeInForce,
            Quantity = request.Quantity,
            LimitPrice = request.Type == OrderType.Market ? null : request.LimitPrice,
            ReduceOnly = request.ReduceOnly,
            Status = OrderStatus.Received,
            Source = strategy is null ? OrderSource.Ticket : OrderSource.Strategy,
            SubmittedBy = strategy is null ? actor.Name : $"Strategy: {strategy.Name}",
            StrategyKeyId = strategy?.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        order.Events.Add(new OrderEvent { At = now, Kind = OrderEventKind.Received, Quantity = order.Quantity, Price = order.LimitPrice, Detail = strategy is null ? "From the order ticket." : "From the strategy interface." });
        database.Orders.Add(order);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same name arrived twice at the same instant; the other one won.
            database.DiscardChanges();
            Order? won = await database.Orders.AsNoTracking().Include(o => o.TradingAccount).ThenInclude(a => a.ManagerAccount).Include(o => o.Events).FirstOrDefaultAsync(o => o.TradingAccountId == account.Id && o.ClientOrderId == clientOrderId, cancellationToken);
            return won is null ? TradingChange<OrderDto>.Refused("The order could not be written down. Nothing was traded; send it again.") : TradingChange<OrderDto>.Ok(Dto(won));
        }

        await signal.WakeAsync(cancellationToken);
        return TradingChange<OrderDto>.Ok(Dto(order));
    }

    /// <summary>Asks for an order to be cancelled. The execution service does the cancelling.</summary>
    public async Task<TradingChange<OrderDto>> CancelAsync(long orderId, Actor actor, StrategyKey? strategy, CancellationToken cancellationToken)
    {
        // The execution service may be writing a fill onto the same order at this very moment. Then this save loses,
        // and the order is read again and the request made on what it has become.
        for (int attempt = 1; ; attempt++)
        {
            Order? order = await database.Orders.Include(o => o.TradingAccount).ThenInclude(a => a.ManagerAccount).Include(o => o.Events).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order is null || (strategy is not null && !AccountIds(strategy).Contains(order.TradingAccountId)))
            {
                return TradingChange<OrderDto>.Missing;
            }

            if (!order.IsOpen)
            {
                return TradingChange<OrderDto>.Refused($"The order is already {order.Status.ToString().ToLowerInvariant()}.");
            }

            if (!order.CancelRequested)
            {
                order.CancelRequested = true;
                order.Events.Add(new OrderEvent { At = clock.GetUtcNow(), Kind = OrderEventKind.CancelRequested, Detail = $"Asked for by {(strategy is null ? actor.Name : "strategy " + strategy.Name)}." });
                try
                {
                    await database.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException) when (attempt < 5)
                {
                    database.DiscardChanges();
                    continue;
                }

                await signal.WakeAsync(cancellationToken);
            }

            return TradingChange<OrderDto>.Ok(Dto(order));
        }
    }

    /// <summary>The account an order goes to: the one named, or the manager's account showing the best price for this side.</summary>
    private async Task<TradingAccount?> PickAccountAsync(SubmitOrderRequest request, string asset, CancellationToken cancellationToken)
    {
        IQueryable<TradingAccount> accounts = database.TradingAccounts.Include(a => a.ManagerAccount).ThenInclude(m => m.Sleeve).ThenInclude(s => s.Fund);
        if (request.TradingAccountId is int accountId)
        {
            return await accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        }

        if (request.ManagerId is not int managerId)
        {
            return null;
        }

        List<TradingAccount> own = await accounts.Where(a => a.ManagerAccountId == managerId).OrderBy(a => a.Id).ToListAsync(cancellationToken);
        if (own.Count <= 1)
        {
            return own.FirstOrDefault();
        }

        IReadOnlyList<VenueBook> live = await books.ReadAsync(asset, request.InstrumentType, cancellationToken);
        // The price after the venue's fee for taking: that, not the raw price, is what the order pays.
        TradingAccount? best = own.Select(a => (Account: a, Book: ConsolidatedBook.Pick(live, a.Venue, a.QuoteFor(request.InstrumentType))))
            .Where(x => (request.Side == OrderSide.Buy ? x.Book?.BestAsk : x.Book?.BestBid) is not null)
            .OrderBy(x =>
            {
                decimal taker = VenueFees.Of(x.Account.Venue, request.InstrumentType).Taker;
                return request.Side == OrderSide.Buy ? x.Book!.BestAsk!.Value * (1m + taker) : -x.Book!.BestBid!.Value * (1m - taker);
            })
            .Select(x => x.Account)
            .FirstOrDefault();
        return best ?? own[0];
    }

    // ---------------------------------------------------------------- strategy keys

    public async Task<IReadOnlyList<StrategyKeyDto>> StrategyKeysAsync(CancellationToken cancellationToken) =>
        (await database.StrategyKeys.AsNoTracking().OrderBy(k => k.Id).ToListAsync(cancellationToken)).Select(Dto).ToList();

    public async Task<TradingChange<StrategyKeyCreatedDto>> AddStrategyKeyAsync(SaveStrategyKeyRequest request, Actor actor, CancellationToken cancellationToken)
    {
        string name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 100 || request.AccountIds is not { Count: > 0 and <= 50 } || request.MaxOrderNotionalUsd is <= 0m or > 1_000_000_000m)
        {
            return TradingChange<StrategyKeyCreatedDto>.Refused("Give a name, at least one trading account, and a largest order above zero.");
        }

        int[] wanted = request.AccountIds.Distinct().ToArray();
        if (await database.TradingAccounts.CountAsync(a => wanted.Contains(a.Id), cancellationToken) != wanted.Length)
        {
            return TradingChange<StrategyKeyCreatedDto>.Refused("One of those trading accounts does not exist.");
        }

        // 256 random bits; the prefix says what kind of key it is and helps tell keys apart.
        string secret = "kft_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        StrategyKey key = new()
        {
            Name = name,
            Prefix = secret[..12],
            KeyHash = Hash(secret),
            AccountIds = string.Join(',', wanted),
            MaxOrderNotionalUsd = request.MaxOrderNotionalUsd,
            CreatedBy = actor.Name,
            CreatedAt = clock.GetUtcNow(),
        };
        database.StrategyKeys.Add(key);
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<StrategyKeyCreatedDto>.Ok(new StrategyKeyCreatedDto(Dto(key), secret));
    }

    public async Task<TradingChange<int>> SetStrategyKeyEnabledAsync(int keyId, bool enabled, CancellationToken cancellationToken)
    {
        StrategyKey? key = await database.StrategyKeys.FirstOrDefaultAsync(k => k.Id == keyId, cancellationToken);
        if (key is null)
        {
            return TradingChange<int>.Missing;
        }

        key.IsEnabled = enabled;
        await database.SaveChangesAsync(cancellationToken);
        return TradingChange<int>.Ok(key.Id);
    }

    /// <summary>The strategy key a secret belongs to, if it is one that is switched on.</summary>
    public async Task<StrategyKey?> FindStrategyKeyAsync(string? secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 200)
        {
            return null;
        }

        string hash = Hash(secret.Trim());
        StrategyKey? key = await database.StrategyKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.IsEnabled, cancellationToken);
        if (key is not null && (key.LastUsedAt is null || clock.GetUtcNow() - key.LastUsedAt.Value > TimeSpan.FromMinutes(1)))
        {
            key.LastUsedAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
        }

        return key;
    }

    public static IReadOnlyList<int> AccountIds(StrategyKey key) => key.AccountIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

    // ---------------------------------------------------------------- helpers

    private static string? Limits(SaveTradingAccountRequest r) =>
        string.IsNullOrWhiteSpace(r.Label) || r.Label.Length > 100 ? "Give the account a label of up to 100 characters."
        : r.Leverage is < 1m or > PreTradeChecks.MaxLeverage ? $"Leverage is between 1 and {PreTradeChecks.MaxLeverage:0}."
        : !Enum.IsDefined(r.MarginMode) ? "The margin mode is cross or isolated."
        : r.MaxOrderNotionalUsd is <= 0m or > 1_000_000_000m ? "The largest order must be above zero."
        : r.MaxPositionNotionalUsd is <= 0m or > 10_000_000_000m ? "The largest position must be above zero."
        : r.MaxOrderNotionalUsd > r.MaxPositionNotionalUsd ? "The largest order cannot be larger than the largest position."
        : r.PriceBandBps is < 10 or > 2000 ? "The price band is between 0.1% and 20%."
        : r.PaperBalanceUsd is < 0m or > 10_000_000_000m ? "The paper balance cannot be negative."
        : null;

    private ApprovalRequest Ask(ApprovalKind kind, int? fundId, int? accountId, int? haltId, string summary, object payload, Actor actor)
    {
        ApprovalRequest request = new()
        {
            Kind = kind,
            FundId = fundId,
            TradingAccountId = accountId,
            TradingHaltId = haltId,
            Summary = summary.Length <= 600 ? summary : summary[..600],
            PayloadJson = JsonSerializer.Serialize(payload, Json),
            RequestedBy = actor.Name,
            RequestedAt = clock.GetUtcNow(),
        };
        database.ApprovalRequests.Add(request);
        return request;
    }

    private static bool Covers(IEnumerable<TradingHalt> halts, TradingAccount account) =>
        halts.Any(h => h.Scope == HaltScope.Global || (h.Scope == HaltScope.Venue && h.Venue == account.Venue) || (h.Scope == HaltScope.Account && h.TradingAccountId == account.Id));

    private async Task<string> WhereAsync(TradingHalt halt, CancellationToken cancellationToken) =>
        halt.Scope switch
        {
            HaltScope.Global => "all trading",
            HaltScope.Venue => VenueExposureService.NameOf(halt.Venue!.Value),
            _ => await database.TradingAccounts.Where(a => a.Id == halt.TradingAccountId).Select(a => a.ManagerAccount.Name + " · " + a.Label).FirstOrDefaultAsync(cancellationToken) ?? "an account",
        };

    private static HaltDto Dto(TradingHalt h, List<TradingAccount> accounts, bool releaseRequested)
    {
        TradingAccount? account = accounts.FirstOrDefault(a => a.Id == h.TradingAccountId);
        string where = h.Scope switch
        {
            HaltScope.Global => "All trading, every fund",
            HaltScope.Venue => $"{VenueExposureService.NameOf(h.Venue!.Value)}, every fund",
            _ => account is null ? "An account" : $"{account.ManagerAccount.Name} on {VenueExposureService.NameOf(account.Venue)}",
        };
        return new HaltDto(h.Id, h.Scope, h.Venue, h.TradingAccountId, where, h.Reason, h.PulledBy, h.PulledAt, releaseRequested);
    }

    private static ApprovalDto Dto(ApprovalRequest r, Actor actor)
    {
        bool own = string.Equals(r.RequestedBy, actor.Name, StringComparison.OrdinalIgnoreCase);
        bool pending = r.Status == ApprovalStatus.Pending;
        return new ApprovalDto(r.Id, r.Kind, r.Summary, r.RequestedBy, r.RequestedAt, r.Status, r.DecidedBy, r.DecidedAt, r.Note, pending && actor.IsAdmin && !own, pending && own);
    }

    private static StrategyKeyDto Dto(StrategyKey k) => new(k.Id, k.Name, k.Prefix, AccountIds(k), k.MaxOrderNotionalUsd, k.IsEnabled, k.CreatedBy, k.CreatedAt, k.LastUsedAt);

    private static OrderDto Dto(Order o)
    {
        decimal? slippage = o is { FilledQuantity: > 0m, ArrivalPrice: > 0m } ? Math.Round((o.AverageFillPrice - o.ArrivalPrice.Value) / o.ArrivalPrice.Value * 10_000m * (o.Side == OrderSide.Buy ? 1m : -1m), 2) : null;
        return new OrderDto(
            o.Id,
            o.ClientOrderId,
            o.TradingAccountId,
            $"{o.TradingAccount.ManagerAccount.Name} · {VenueExposureService.NameOf(o.TradingAccount.Venue)}",
            o.TradingAccount.Venue,
            o.Asset,
            o.InstrumentType,
            o.Side,
            o.Type,
            o.TimeInForce,
            o.Quantity,
            o.LimitPrice,
            o.ReduceOnly,
            o.Status,
            o.FilledQuantity,
            o.AverageFillPrice,
            o.FeesUsd,
            o.ArrivalPrice,
            slippage,
            o.RejectReason,
            o.CancelRequested,
            o.Source,
            o.SubmittedBy,
            o.CreatedAt,
            o.UpdatedAt,
            // In the order they were written down. The web service and the execution service each stamp their own
            // events, and their clocks need not agree to the millisecond.
            o.Events.OrderBy(e => e.Id).Select(e => new OrderEventDto(e.At, e.Kind, e.Venue, e.Quantity, e.Price, e.FeeUsd, e.Detail)).ToList()
        );
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static bool IsHex(string text, int digits) => text.Length == digits + 2 && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text[2..].All(Uri.IsHexDigit);
}

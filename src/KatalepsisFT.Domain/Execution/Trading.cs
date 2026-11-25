using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Domain.Execution;

public enum TradingMode
{
    /// <summary>Orders are filled against the live book by this system; nothing is sent to the venue.</summary>
    Paper = 1,

    /// <summary>Orders go to the venue with real money. Needs a trading key and a second person's approval.</summary>
    Live = 2,
}

public enum MarginMode
{
    Cross = 1,
    Isolated = 2,
}

/// <summary>
/// One manager's account on one venue, as the execution side sees it: how it may trade and within what limits. It is
/// separate from the read-only connection that monitoring uses, and its key lives in a vault of its own.
/// </summary>
public class TradingAccount
{
    public int Id { get; set; }
    public int ManagerAccountId { get; set; }
    public ManagerAccount ManagerAccount { get; set; } = null!;
    public Venue Venue { get; set; }
    public required string Label { get; set; }
    public TradingMode Mode { get; set; } = TradingMode.Paper;

    /// <summary>The trading key, encrypted under the execution master key. Null while the account only paper-trades.</summary>
    public string? EncryptedKey { get; set; }

    /// <summary>The last characters of the key, to tell keys apart on screen.</summary>
    public string? KeyHint { get; set; }

    /// <summary>Leverage this system sets on the venue for every instrument of the account; never the venue's default.</summary>
    public decimal Leverage { get; set; } = 1m;
    public MarginMode MarginMode { get; set; } = MarginMode.Cross;

    /// <summary>
    /// What the account's perpetuals are margined and quoted in: USDT nearly everywhere, or USDC where the venue
    /// also lists those. One account trades one family; a manager who wants both has two accounts.
    /// </summary>
    public string PerpQuote { get; set; } = "USDT";

    /// <summary>
    /// What the account was worth when it was last counted in its manager's book; empty while it is not counted.
    /// Coming into the book and leaving it are recorded as money arriving and leaving, at this value, so that
    /// neither shows up as a gain or a loss.
    /// </summary>
    public decimal? InBookEquityUsd { get; set; }

    /// <summary>
    /// Whether it was counted as a paper or a real-money account. The two are different money: an account that
    /// changes from one to the other leaves the book as the one and comes into it as the other.
    /// </summary>
    public TradingMode? InBookMode { get; set; }

    /// <summary>The currency this account's book for a kind of market must be in; null where the venue has only one.</summary>
    public string? QuoteFor(InstrumentType type) => type == InstrumentType.Perp && PerpQuote != "USDT" ? PerpQuote : null;

    /// <summary>The largest single order, in USD of notional.</summary>
    public decimal MaxOrderNotionalUsd { get; set; }

    /// <summary>The largest position in any one instrument, in USD of notional, long or short.</summary>
    public decimal MaxPositionNotionalUsd { get; set; }

    /// <summary>How far from the market a limit price may be, in hundredths of a percent.</summary>
    public int PriceBandBps { get; set; } = 500;

    /// <summary>What a paper account starts with, in USD.</summary>
    public decimal PaperBalanceUsd { get; set; }

    /// <summary>For a real account: what the venue says the account is worth, in USD, at the last reading.</summary>
    public decimal? LiveEquityUsd { get; set; }

    /// <summary>When the venue's account was last read successfully.</summary>
    public DateTimeOffset? LiveSyncedAt { get; set; }

    /// <summary>Why the last reading of the venue's account failed; null when it worked.</summary>
    public string? LiveSyncError { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HaltScope
{
    /// <summary>Every account on every venue.</summary>
    Global = 1,
    Venue = 2,
    Account = 3,
}

/// <summary>
/// A kill switch that has been pulled. While it is active no new order is accepted in its scope and open orders
/// there are cancelled. Pulling takes one person; releasing takes two.
/// </summary>
public class TradingHalt
{
    public int Id { get; set; }
    public HaltScope Scope { get; set; }
    public Venue? Venue { get; set; }
    public int? TradingAccountId { get; set; }
    public required string Reason { get; set; }
    public required string PulledBy { get; set; }
    public DateTimeOffset PulledAt { get; set; }
    public string? ReleasedBy { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }

    public bool IsActive => ReleasedAt is null;
}

public enum ApprovalKind
{
    /// <summary>Store a trading key on an account.</summary>
    AddTradingKey = 1,

    /// <summary>Raise an account's order, position or leverage limit.</summary>
    RaiseLimits = 2,

    /// <summary>Let an account trade again after its kill switch was pulled.</summary>
    ReleaseHalt = 3,

    /// <summary>Move an account from paper to real money.</summary>
    GoLive = 4,

    /// <summary>Start a working order larger than its account may send without a second person.</summary>
    LargeOrder = 5,
}

public enum ApprovalStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,

    /// <summary>Withdrawn by whoever asked.</summary>
    Cancelled = 4,
}

/// <summary>
/// A change that needs two people: one asks, another approves, and only then does it take effect. The person who
/// asked can never approve their own request.
/// </summary>
public class ApprovalRequest
{
    public int Id { get; set; }
    public ApprovalKind Kind { get; set; }
    public int? FundId { get; set; }
    public int? TradingAccountId { get; set; }
    public int? TradingHaltId { get; set; }

    /// <summary>What is asked for, in words, as both people see it.</summary>
    public required string Summary { get; set; }

    /// <summary>The change itself, as the system will apply it. A key in here is already encrypted.</summary>
    public required string PayloadJson { get; set; }
    public required string RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// A key a strategy uses to send orders. Only a hash of it is kept; the key itself is shown once, when it is made.
/// It can trade only the accounts it is tied to, within its own order limit.
/// </summary>
public class StrategyKey
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>The first characters of the key, to tell keys apart on screen.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the key, in hex.</summary>
    public required string KeyHash { get; set; }

    /// <summary>Ids of the trading accounts it may trade, separated by commas.</summary>
    public required string AccountIds { get; set; }
    public decimal MaxOrderNotionalUsd { get; set; }
    public bool IsEnabled { get; set; } = true;
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>A named value the system keeps for itself, such as the public half of the trading-key vault.</summary>
public class SystemSetting
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Value { get; set; }
}

/// <summary>
/// An instrument a venue lists and this system can keep a book for. The list is read from every venue each hour, so
/// that any of them can be searched for and traded, not only a handful named in a setting.
/// </summary>
public class ListedInstrument
{
    public long Id { get; set; }
    public Venue Venue { get; set; }
    public InstrumentType Type { get; set; }

    /// <summary>Our name for the asset: capital letters and digits, the same on every venue.</summary>
    public required string Asset { get; set; }

    /// <summary>What the venue's prices for it are in.</summary>
    public required string Quote { get; set; }

    /// <summary>The venue's own name for the instrument.</summary>
    public required string Symbol { get; set; }

    /// <summary>False when the venue has it listed but is not taking orders, or no longer lists it.</summary>
    public bool Tradable { get; set; }

    /// <summary>The smallest step in price the venue takes, where its list says.</summary>
    public decimal? PriceStep { get; set; }

    /// <summary>The smallest step in quantity, in units of the asset, where the venue's list says.</summary>
    public decimal? QuantityStep { get; set; }

    /// <summary>The smallest order, in units of the asset, where the venue's list says.</summary>
    public decimal? MinQuantity { get; set; }

    /// <summary>When the venue's list last included it.</summary>
    public DateTimeOffset SeenAt { get; set; }
}

/// <summary>
/// An instrument a fund keeps at hand on its trading page. Its book stays open whether or not anyone is looking, so
/// it is there at once when it is needed.
/// </summary>
public class PinnedInstrument
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public required string Asset { get; set; }
    public InstrumentType Type { get; set; }
}

using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Domain.Execution;

public enum OrderSide
{
    Buy = 1,
    Sell = 2,
}

public enum OrderType
{
    Market = 1,
    Limit = 2,
}

public enum TimeInForce
{
    /// <summary>Stays in the book until filled or cancelled.</summary>
    GoodTillCancelled = 1,

    /// <summary>Fills what it can at once; the rest is cancelled.</summary>
    ImmediateOrCancel = 2,

    /// <summary>Only ever adds to the book; refused if it would trade at once.</summary>
    PostOnly = 3,
}

public enum OrderStatus
{
    /// <summary>Written down, not yet looked at by the execution service.</summary>
    Received = 1,

    /// <summary>Passed every check; about to go out.</summary>
    Accepted = 2,

    /// <summary>In the market, nothing filled yet.</summary>
    Working = 3,
    PartlyFilled = 4,
    Filled = 5,
    Cancelled = 6,

    /// <summary>Failed a check or was refused by the venue; nothing was traded.</summary>
    Rejected = 7,
}

public enum OrderSource
{
    /// <summary>A person, through the order ticket.</summary>
    Ticket = 1,

    /// <summary>A strategy, through the API.</summary>
    Strategy = 2,

    /// <summary>A slice of a working order, sent by the system on its behalf.</summary>
    Working = 3,
}

/// <summary>An order as it stands now. Its history is in <see cref="OrderEvent"/>; the two are always saved together.</summary>
public class Order
{
    public long Id { get; set; }

    /// <summary>The sender's own name for the order. The same name twice is the same order, never a second one.</summary>
    public required string ClientOrderId { get; set; }
    public int TradingAccountId { get; set; }
    public TradingAccount TradingAccount { get; set; } = null!;

    /// <summary>The asset, in capitals: BTC.</summary>
    public required string Asset { get; set; }

    /// <summary>Spot or Perp.</summary>
    public InstrumentType InstrumentType { get; set; }
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public TimeInForce TimeInForce { get; set; }

    /// <summary>In units of the asset.</summary>
    public decimal Quantity { get; set; }
    public decimal? LimitPrice { get; set; }

    /// <summary>May only shrink the position, never open or flip one.</summary>
    public bool ReduceOnly { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Received;
    public decimal FilledQuantity { get; set; }
    public decimal AverageFillPrice { get; set; }
    public decimal FeesUsd { get; set; }

    /// <summary>The middle of the market when the order arrived: what its fills are judged against.</summary>
    public decimal? ArrivalPrice { get; set; }
    public string? RejectReason { get; set; }

    /// <summary>Somebody asked for it to be cancelled; the execution service has yet to act.</summary>
    public bool CancelRequested { get; set; }
    public OrderSource Source { get; set; }

    /// <summary>The person, or the strategy key's name.</summary>
    public required string SubmittedBy { get; set; }
    public int? StrategyKeyId { get; set; }

    /// <summary>The working order this order is a slice of, if it is one.</summary>
    public long? WorkingOrderId { get; set; }
    public WorkingOrder? WorkingOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrderEvent> Events { get; set; } = [];

    /// <summary>
    /// For an order on a real account: when it was handed to the venue. It is written down before the order goes
    /// out, so an order with this set and no answer on record is one whose fate has to be asked of the venue,
    /// never one to send again.
    /// </summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>The name the venue knows the order by. Ours, so the order can be found there even if the answer to sending it was lost.</summary>
    public string? VenueClientId { get; set; }

    /// <summary>The venue's own id for the order, once it has answered.</summary>
    public string? VenueOrderId { get; set; }

    /// <summary>Changes with every save. A save based on an older reading fails, so two processes can never both act on one order.</summary>
    public uint Version { get; set; }

    public bool IsOpen => Status is OrderStatus.Received or OrderStatus.Accepted or OrderStatus.Working or OrderStatus.PartlyFilled;

    public decimal RemainingQuantity => Quantity - FilledQuantity;
}

public enum OrderEventKind
{
    Received = 1,
    Accepted = 2,
    Rejected = 3,
    Working = 4,
    Fill = 5,
    CancelRequested = 6,
    Cancelled = 7,

    /// <summary>Something worth knowing that did not change the order's state.</summary>
    Note = 8,
}

/// <summary>One thing that happened to an order. Only ever added: the full list is the order's record.</summary>
public class OrderEvent
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public DateTimeOffset At { get; set; }
    public OrderEventKind Kind { get; set; }

    /// <summary>For a fill: where it traded.</summary>
    public Venue? Venue { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? FeeUsd { get; set; }
    public string? Detail { get; set; }
}

/// <summary>What a trading account holds in one instrument, as the execution service counts it from its own fills.</summary>
public class TradingPosition
{
    public int Id { get; set; }
    public int TradingAccountId { get; set; }
    public TradingAccount TradingAccount { get; set; } = null!;
    public required string Asset { get; set; }
    public InstrumentType InstrumentType { get; set; }

    /// <summary>Signed: negative is short.</summary>
    public decimal Quantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal RealisedPnlUsd { get; set; }
    public decimal FeesUsd { get; set; }

    /// <summary>For a leveraged position on a real account: the price at which the venue would close it by force, as the venue reports it.</summary>
    public decimal? LiquidationPrice { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>How a working order is fed into the market.</summary>
public enum WorkingStyle
{
    /// <summary>Even slices across a set time.</summary>
    OverTime = 1,

    /// <summary>Slices of a size picked at random between two bounds, with a wait between them, for as long as it takes.</summary>
    Paced = 2,

    /// <summary>Rests at the best price as a maker and follows the market; never crosses the spread.</summary>
    Passive = 3,
}

public enum WorkingState
{
    /// <summary>Larger than its account may send without a second person; nothing is sent until someone approves.</summary>
    PendingApproval = 1,
    Working = 2,

    /// <summary>Held by a person or by one of its own guards; nothing more is sent until it is resumed.</summary>
    Paused = 3,

    /// <summary>All of it has traded.</summary>
    Done = 4,

    /// <summary>Ended before all of it traded: by a person, the kill switch, its time running out or repeated refusals.</summary>
    Stopped = 5,

    /// <summary>The second person said no.</summary>
    Rejected = 6,
}

/// <summary>
/// An order too large to send at once, worked over time. It is a plan with guards; what actually goes to the market
/// are its slices, each an ordinary order with the working order's id on it, checked and filled like any other. What
/// has been filled is always the sum of the slices, so a restart loses nothing: the engine reads the slices back and
/// carries on from there.
/// </summary>
public class WorkingOrder
{
    public long Id { get; set; }

    /// <summary>The sender's own name for it; the same name again returns the same working order.</summary>
    public required string ClientId { get; set; }
    public int ManagerAccountId { get; set; }
    public ManagerAccount ManagerAccount { get; set; } = null!;

    /// <summary>The one account to work it on; empty lets each slice go to the manager's account with the best price.</summary>
    public int? TradingAccountId { get; set; }
    public TradingAccount? TradingAccount { get; set; }
    public required string Asset { get; set; }
    public InstrumentType InstrumentType { get; set; }
    public OrderSide Side { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>No slice trades beyond this price; empty for none.</summary>
    public decimal? LimitPrice { get; set; }
    public bool ReduceOnly { get; set; }
    public WorkingStyle Style { get; set; }

    /// <summary>Over time: how long the whole order is spread over.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Over time: how many slices it is cut into.</summary>
    public int? Slices { get; set; }

    /// <summary>Paced: the smallest and largest slice, in units of the asset.</summary>
    public decimal? MinSlice { get; set; }
    public decimal? MaxSlice { get; set; }

    /// <summary>Paced: the wait between slices.</summary>
    public int? WaitSeconds { get; set; }

    /// <summary>Buying stops while the market is above this, selling while it is below; it carries on when the price comes back.</summary>
    public decimal? PausePrice { get; set; }

    /// <summary>The order pauses itself when its average price is this much worse than the price when it started.</summary>
    public int? MaxSlippageBps { get; set; }

    /// <summary>Set when a person resumes an order its slippage guard had paused: the guard has done its job and is not applied again.</summary>
    public bool SlippageAccepted { get; set; }
    public WorkingState State { get; set; }

    /// <summary>In words: why it is waiting, paused or stopped.</summary>
    public string? Note { get; set; }
    public bool StopRequested { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal AverageFillPrice { get; set; }
    public decimal FeesUsd { get; set; }

    /// <summary>The middle of the market when the first slice was sent.</summary>
    public decimal? ArrivalPrice { get; set; }
    public int SlicesSent { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? NextSliceAt { get; set; }

    /// <summary>The order stops by itself at this moment, filled or not.</summary>
    public DateTimeOffset? HardEndAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ApprovedBy { get; set; }

    /// <summary>The same on every working order that was sent together as one basket; empty for one sent on its own.</summary>
    public string? Basket { get; set; }
    public List<Order> Children { get; set; } = [];

    /// <summary>Changes on every save, so that a person's pause and the engine's own write cannot overwrite each other unseen.</summary>
    public uint Version { get; set; }

    public bool IsOpen => State is WorkingState.PendingApproval or WorkingState.Working or WorkingState.Paused;
    public decimal RemainingQuantity => Quantity - FilledQuantity;
}

using KatalepsisFT.Domain.Connections;
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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrderEvent> Events { get; set; } = [];

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
    public DateTimeOffset UpdatedAt { get; set; }
}

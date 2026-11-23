using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Application.Execution;

/// <summary>A venue's rules for one instrument: the steps quantities and prices move in, and the smallest order.</summary>
/// <param name="Symbol">The venue's own name for the instrument.</param>
/// <param name="QuantityStep">Quantities are whole multiples of this, in units of the asset. For a perpetual it is the size of one contract.</param>
/// <param name="MinNotional">The smallest order by value, in the quote currency; zero when the venue has no such rule.</param>
public sealed record VenueInstrument(string Symbol, decimal QuantityStep, decimal PriceStep, decimal MinQuantity, decimal MinNotional)
{
    /// <summary>The quantity cut down to a whole number of steps: never more than was asked for.</summary>
    public decimal FloorQuantity(decimal quantity) => QuantityStep <= 0m ? quantity : Math.Floor(quantity / QuantityStep) * QuantityStep;

    /// <summary>A limit price moved onto the venue's price grid, never to a worse price than was given: a buy is rounded down, a sell up.</summary>
    public decimal RoundPrice(decimal price, OrderSide side) =>
        PriceStep <= 0m ? price : (side == OrderSide.Buy ? Math.Floor(price / PriceStep) : Math.Ceiling(price / PriceStep)) * PriceStep;
}

/// <param name="ClientId">The name the venue will know the order by. Sending the same name twice must never make two orders.</param>
public sealed record VenueOrderRequest(string ClientId, InstrumentType Type, OrderSide Side, OrderType OrderType, TimeInForce TimeInForce, decimal Quantity, decimal? LimitPrice, bool ReduceOnly, decimal Leverage, MarginMode MarginMode);

public enum VenueOrderState
{
    /// <summary>The venue has no order by that name.</summary>
    NotFound = 1,

    /// <summary>Still in the market.</summary>
    Open = 2,

    /// <summary>Finished: filled, cancelled, or part of each.</summary>
    Done = 3,
}

/// <param name="FilledQuantity">How much has traded so far, in units of the asset.</param>
/// <param name="FilledValue">What that was worth, in the quote currency.</param>
/// <param name="FeeUsd">Fees charged so far, when the venue reports them in a dollar currency; null when it does not (yet).</param>
public sealed record VenueOrderReport(VenueOrderState State, decimal FilledQuantity, decimal FilledValue, decimal? FeeUsd, string? VenueOrderId, string? Note = null);

/// <param name="Quantity">Signed for a perpetual; what is held for spot.</param>
/// <param name="AveragePrice">The venue's entry price of a perpetual position; spot has none.</param>
/// <param name="PriceUsd">What the venue says one unit is worth now.</param>
public sealed record VenueHolding(string Asset, InstrumentType Type, decimal Quantity, decimal? AveragePrice, decimal? LiquidationPrice, decimal? PriceUsd = null);

/// <param name="CashUsd">Dollar currencies in the spot account.</param>
/// <param name="DerivativesEquityUsd">What the futures account is worth, open profit and loss included; null when it could not be read.</param>
public sealed record VenueAccountReport(decimal CashUsd, decimal? DerivativesEquityUsd, IReadOnlyList<VenueHolding> Holdings);

/// <summary>The venue answered, and the answer is no. Nothing was done; the message is the venue's reason.</summary>
public sealed class VenueRefusedException(string message) : Exception(message);

/// <summary>
/// The venue did not give an answer that can be relied on: no reply, a timeout, a server error. For an order that
/// was being sent, this means it may or may not have arrived.
/// </summary>
public sealed class VenueUnclearException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sending real orders to one venue. Every call is one request; nothing is retried in here.</summary>
public interface IOrderGateway
{
    Venue Venue { get; }

    /// <summary>The venue's rules for the instrument, or null when the venue does not list it.</summary>
    Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken);

    /// <summary>Sends the order. Throws <see cref="VenueRefusedException"/> or <see cref="VenueUnclearException"/>.</summary>
    /// <returns>The venue's id for the order.</returns>
    Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken);

    /// <summary>Asks for the order to be cancelled. That the request was taken does not mean the order is gone: ask with <see cref="FindAsync"/>.</summary>
    Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, string clientId, CancellationToken cancellationToken);

    /// <summary>What the venue knows of the order by our name for it.</summary>
    Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, string clientId, CancellationToken cancellationToken);

    /// <summary>What the account holds right now.</summary>
    Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken);
}

/// <summary>Opens stored trading keys. Only the execution service has one that works.</summary>
public interface ITradingKeys
{
    /// <summary>False until the vault has been prepared, and for ever where there is no execution master key.</summary>
    bool Ready { get; }

    VenueCredentials Open(string sealedKey);
}

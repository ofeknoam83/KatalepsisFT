using FluentAssertions;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Connections;

namespace KatalepsisFT.Web.Tests.Execution;

/// <summary>The limits every order passes, tried directly: orders already working count towards them.</summary>
public class PreTradeCheckTests
{
    // BTC at 100,000: bid 99,990, ask 100,010.
    private static readonly VenueBook Book = new(Venue.Binance, "BTC", InstrumentType.Perp, [new BookLevel(99_990m, 5m)], [new BookLevel(100_010m, 5m)], DateTimeOffset.UtcNow);

    private static TradingAccount Account() =>
        new()
        {
            Label = "Main",
            CreatedBy = "test",
            Venue = Venue.Binance,
            MaxOrderNotionalUsd = 250_000m,
            MaxPositionNotionalUsd = 100_000m,
            PriceBandBps = 100,
            Leverage = 3m,
        };

    private static Order Sell(decimal quantity, InstrumentType type = InstrumentType.Perp) =>
        new()
        {
            ClientOrderId = Guid.NewGuid().ToString("N"),
            Asset = "BTC",
            SubmittedBy = "test",
            InstrumentType = type,
            Side = OrderSide.Sell,
            Type = OrderType.Limit,
            TimeInForce = TimeInForce.GoodTillCancelled,
            Quantity = quantity,
            LimitPrice = 100_500m,
        };

    private static string? Check(Order order, decimal position, decimal openSells) => PreTradeChecks.Check(order, new CheckContext(Account(), position, Book, false, null, 0m, openSells));

    [Fact]
    public void PositionLimit_CountsSellsAlreadyWorking_SoReducingOrdersCannotTurnThePositionRoundPastIt()
    {
        // Long 1 BTC with a limit of 100,000 USD, which is 1 BTC.
        string? first = Check(Sell(2m), position: 1m, openSells: 0m);
        string? second = Check(Sell(2m), position: 1m, openSells: 2m);
        string? closing = Check(Sell(1m), position: 1m, openSells: 0m);

        first.Should().BeNull("on its own it leaves the account short 1 BTC, which is within the limit");
        second.Should().Contain("largest position", "with the first still working the account could end short 3 BTC");
        closing.Should().BeNull("an order that only brings the position to nothing is always let through");
    }

    [Fact]
    public void PositionLimit_StillLetsAnOversizedPositionBeReduced()
    {
        // Long 3 BTC, three times the limit: selling some of it must not be refused.
        Check(Sell(1m), position: 3m, openSells: 1m).Should().BeNull();
    }

    [Fact]
    public void Spot_CannotBeSoldTwice()
    {
        string? first = Check(Sell(1m, InstrumentType.Spot), position: 1m, openSells: 0m);
        string? second = Check(Sell(1m, InstrumentType.Spot), position: 1m, openSells: 1m);

        first.Should().BeNull();
        second.Should().Contain("already being sold");
    }
}

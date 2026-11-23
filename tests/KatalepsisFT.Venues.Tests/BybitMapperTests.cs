using Bybit.Net.Enums;
using Bybit.Net.Objects.Models.V5;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Bybit;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// The Bybit objects here are built by hand from the field meanings in Bybit's API documentation; they are not
/// recordings of a real account, and must be confirmed against a real read-only key.
/// </summary>
public class BybitMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static BybitBalance Balance(params BybitAssetBalance[] assets) => new() { TotalEquity = 250_000m, Assets = assets };

    [Fact]
    public void Balances_UseTheWalletBalance_PricedFromTheVenuesUsdValue()
    {
        BybitBalance balance = Balance(
            new BybitAssetBalance { Asset = "USDT", WalletBalance = 20_000m, Equity = 20_300m, UsdValue = 20_300m },
            new BybitAssetBalance { Asset = "BTC", WalletBalance = 2m, Equity = 2m, UsdValue = 160_000m },
            new BybitAssetBalance { Asset = "ETH", WalletBalance = 0m, Equity = 0m, UsdValue = 0m }
        );

        VenueSnapshot snapshot = BybitMapper.Map(Now, balance, []);

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Cash, "USDT", 20_000m, 1m), new VenuePosition(InstrumentType.Spot, "BTC", 2m, 80_000m));
        snapshot.ReportedEquityUsd.Should().Be(250_000m);
    }

    [Fact]
    public void LinearPerps_TakeTheirSignFromTheSide()
    {
        BybitPosition shortEth = new() { Symbol = "ETHUSDT", Side = PositionSide.Sell, Quantity = 10m, MarkPrice = 3_000m, AveragePrice = 3_100m, UnrealizedPnl = 1_000m };
        BybitPosition longBtcUsdc = new() { Symbol = "BTCPERP", Side = PositionSide.Buy, Quantity = 0.5m, MarkPrice = 80_000m, AveragePrice = 79_000m, UnrealizedPnl = 500m };
        BybitPosition none = new() { Symbol = "SOLUSDT", Side = PositionSide.None, Quantity = 0m };

        VenueSnapshot snapshot = BybitMapper.Map(Now, Balance(), [(Category.Linear, shortEth), (Category.Linear, longBtcUsdc), (Category.Linear, none)]);

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Perp, "ETH", -10m, 3_000m, 3_100m, 1_000m), new VenuePosition(InstrumentType.Perp, "BTC", 0.5m, 80_000m, 79_000m, 500m));
    }

    [Fact]
    public void UsdcFuture_CarriesItsExpiry()
    {
        BybitPosition position = new() { Symbol = "BTC-25DEC26", Side = PositionSide.Buy, Quantity = 1m, MarkPrice = 81_000m, AveragePrice = 80_000m, UnrealizedPnl = 1_000m };

        VenuePosition mapped = BybitMapper.Map(Now, Balance(), [(Category.Linear, position)]).Positions.Single();

        mapped.Should().Be(new VenuePosition(InstrumentType.Future, "BTC", 1m, 81_000m, 80_000m, 1_000m, new DateOnly(2026, 12, 25)));
    }

    [Fact]
    public void InversePerp_SizedInUsd_BecomesCoins_AndItsProfitUsd()
    {
        // 40,000 USD short at 80,000 is 0.5 BTC; 0.01 BTC of open profit is 800 USD.
        BybitPosition position = new() { Symbol = "BTCUSD", Side = PositionSide.Sell, Quantity = 40_000m, MarkPrice = 80_000m, AveragePrice = 82_000m, UnrealizedPnl = 0.01m };

        VenuePosition mapped = BybitMapper.Map(Now, Balance(), [(Category.Inverse, position)]).Positions.Single();

        mapped.Should().Be(new VenuePosition(InstrumentType.Perp, "BTC", -0.5m, 80_000m, 82_000m, 800m));
    }

    [Fact]
    public void InverseQuarterly_ExpiresOnTheLastFridayOfItsMonth()
    {
        BybitPosition position = new() { Symbol = "BTCUSDZ26", Side = PositionSide.Buy, Quantity = 8_000m, MarkPrice = 80_000m, UnrealizedPnl = 0m };

        VenuePosition mapped = BybitMapper.Map(Now, Balance(), [(Category.Inverse, position)]).Positions.Single();

        mapped.Type.Should().Be(InstrumentType.Future);
        mapped.Expiry.Should().Be(new DateOnly(2026, 12, 25));
    }

    [Fact]
    public void Option_PremiumAndDelta_ArePerUnit()
    {
        // Short 2 BTC of calls worth 6,400 USD in all; position delta −0.7.
        BybitPosition position = new() { Symbol = "BTC-25DEC26-90000-C-USDT", Side = PositionSide.Sell, Quantity = 2m, MarkPrice = 3_200m, AveragePrice = 4_000m, PositionValue = 6_400m, Delta = -0.7m };

        // The position's own mark is the option's; the coin's price comes from the spot market.
        VenuePosition mapped = BybitMapper.Map(Now, Balance(), [(Category.Option, position)], new Dictionary<string, decimal> { ["BTC"] = 80_000m }).Positions.Single();

        mapped.Should().Be(new VenuePosition(InstrumentType.Option, "BTC", -2m, 3_200m, 4_000m, null, new DateOnly(2026, 12, 25), 90_000m, OptionRight.Call, 80_000m, 0.35m));
    }

    [Fact]
    public void PositionWithoutAMarkPrice_FailsTheWholeSnapshot()
    {
        BybitPosition position = new() { Symbol = "BTCUSDT", Side = PositionSide.Buy, Quantity = 1m };

        FluentActions.Invoking(() => BybitMapper.Map(Now, Balance(), [(Category.Linear, position)])).Should().Throw<VenueException>().WithMessage("Bybit reported BTCUSDT without a mark price.");
    }
}

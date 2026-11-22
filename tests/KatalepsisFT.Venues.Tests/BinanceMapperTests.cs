using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Spot;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Binance;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// The Binance objects here are built by hand from the field meanings in Binance's API documentation; they are not
/// recordings of a real account, and must be confirmed against a real read-only key.
/// </summary>
public class BinanceMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, decimal> Prices = new() { ["BTCUSDT"] = 80_000m, ["ETHUSDT"] = 3_000m, ["BNBUSDT"] = 600m };

    private static BinanceBalance Spot(string asset, decimal available, decimal locked = 0m) => new() { Asset = asset, Available = available, Locked = locked };

    private static BinanceUsdFuturesAccountBalance Wallet(string asset, decimal balance) => new() { Asset = asset, WalletBalance = balance, CrossUnrealizedPnl = 999m };

    private static BinancePositionDetailsUsdt Position(string symbol, decimal quantity, decimal mark, decimal entry, decimal pnl) =>
        new() { Symbol = symbol, Quantity = quantity, MarkPrice = mark, EntryPrice = entry, UnrealizedPnl = pnl };

    [Fact]
    public void SpotAndFuturesWallets_AreOneLinePerAsset_IncludingLockedAndLentCoins()
    {
        VenueSnapshot snapshot = BinanceMapper.Map(Now, [Spot("BTC", 1m, 0.5m), Spot("LDBTC", 0.25m), Spot("USDT", 1_000m), Spot("ETH", 0m)], Prices, [Wallet("USDT", 4_000m), Wallet("BNB", 2m)], []);

        snapshot.Positions.Should().BeEquivalentTo(
            [new VenuePosition(InstrumentType.Spot, "BNB", 2m, 600m), new VenuePosition(InstrumentType.Spot, "BTC", 1.75m, 80_000m), new VenuePosition(InstrumentType.Cash, "USDT", 5_000m, 1m)]
        );
    }

    [Fact]
    public void Perp_KeepsTheVenuesSign_AndItsOpenProfit()
    {
        VenueSnapshot snapshot = BinanceMapper.Map(Now, [], Prices, [], [Position("ETHUSDT", -10m, 3_000m, 3_100m, 1_000m), Position("BTCUSDC", 0.5m, 80_000m, 79_000m, 500m), Position("SOLUSDT", 0m, 150m, 0m, 0m)]);

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Perp, "ETH", -10m, 3_000m, 3_100m, 1_000m), new VenuePosition(InstrumentType.Perp, "BTC", 0.5m, 80_000m, 79_000m, 500m));
    }

    [Fact]
    public void ContractQuotedPerThousandCoins_IsTurnedIntoSingleCoins()
    {
        VenuePosition mapped = BinanceMapper.Map(Now, [], Prices, [], [Position("1000PEPEUSDT", 2_000m, 0.012m, 0.010m, 4m)]).Positions.Single();

        mapped.Should().Be(new VenuePosition(InstrumentType.Perp, "PEPE", 2_000_000m, 0.000012m, 0.00001m, 4m));
    }

    [Fact]
    public void DatedFuture_CarriesItsExpiry()
    {
        VenuePosition mapped = BinanceMapper.Map(Now, [], Prices, [], [Position("BTCUSDT_261225", 1m, 81_000m, 80_000m, 1_000m)]).Positions.Single();

        mapped.Type.Should().Be(InstrumentType.Future);
        mapped.Expiry.Should().Be(new DateOnly(2026, 12, 25));
    }

    [Fact]
    public void HoldingWithoutAUsdPrice_FailsTheWholeSnapshot()
    {
        FluentActions.Invoking(() => BinanceMapper.Map(Now, [Spot("OBSCURE", 5m)], Prices, [], [])).Should().Throw<VenueException>().WithMessage("Binance holds OBSCURE but lists no USD price for it.");
    }

    [Fact]
    public void PositionWithAnUnreadableName_FailsTheWholeSnapshot()
    {
        FluentActions.Invoking(() => BinanceMapper.Map(Now, [], Prices, [], [Position("BTCEUR", 1m, 1m, 1m, 0m)])).Should().Throw<VenueException>();
    }
}

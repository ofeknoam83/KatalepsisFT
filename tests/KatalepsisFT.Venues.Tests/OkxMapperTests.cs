using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Venues.Okx;
using OKX.Net.Enums;
using OKX.Net.Objects.Account;
using FtInstrumentType = KatalepsisFT.Domain.Instruments.InstrumentType;
using FtOptionRight = KatalepsisFT.Domain.Instruments.OptionRight;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// The OKX objects here are built by hand from the field meanings in OKX's API documentation; they are not recordings
/// of a real account, and must be confirmed against a real read-only key.
/// </summary>
public class OkxMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static OKXAccountBalance Balance(params OKXAccountBalanceDetail[] details) => new() { TotalEquity = 123_456m, Details = details };

    [Fact]
    public void Balances_UseTheCashBalance_PricedFromTheVenuesUsdValue()
    {
        OKXAccountBalance balance = Balance(
            new OKXAccountBalanceDetail { Asset = "USDT", CashBalance = 50_000m, Equity = 50_400m, UsdEquity = 50_400m },
            new OKXAccountBalanceDetail { Asset = "BTC", CashBalance = 1.5m, Equity = 1.6m, UsdEquity = 128_000m },
            new OKXAccountBalanceDetail { Asset = "ETH", CashBalance = 0m, Equity = 0m, UsdEquity = 0m }
        );

        VenueSnapshot snapshot = OkxMapper.Map(Now, balance, []);

        snapshot.Positions.Should().BeEquivalentTo([new VenuePosition(FtInstrumentType.Cash, "USDT", 50_000m, 1m), new VenuePosition(FtInstrumentType.Spot, "BTC", 1.5m, 80_000m)]);
        snapshot.ReportedEquityUsd.Should().Be(123_456m);
        snapshot.AsOf.Should().Be(Now);
    }

    [Fact]
    public void LinearSwap_InNetMode_TakesItsSignFromTheQuantity()
    {
        // 30 contracts of 0.01 BTC short at 80,000: 0.3 BTC, 24,000 USD.
        OKXPosition position = new() { Symbol = "BTC-USDT-SWAP", InstrumentType = InstrumentType.Swap, PositionSide = PositionSide.Net, PositionsQuantity = -30m, MarkPrice = 80_000m, NotionalUsd = 24_000m, AveragePrice = 82_000m, UnrealizedProfitAndLoss = 600m };

        VenuePosition mapped = OkxMapper.Map(Now, Balance(), [position]).Positions.Single();

        mapped.Should().Be(new VenuePosition(FtInstrumentType.Perp, "BTC", -0.3m, 80_000m, 82_000m, 600m));
    }

    [Fact]
    public void Swap_InHedgeMode_TakesItsSignFromTheSide()
    {
        OKXPosition shortSide = new() { Symbol = "ETH-USDT-SWAP", InstrumentType = InstrumentType.Swap, PositionSide = PositionSide.Short, PositionsQuantity = 50m, MarkPrice = 3_000m, NotionalUsd = 15_000m, AveragePrice = 3_100m, UnrealizedProfitAndLoss = 500m };
        OKXPosition longSide = new() { Symbol = "ETH-USDT-SWAP", InstrumentType = InstrumentType.Swap, PositionSide = PositionSide.Long, PositionsQuantity = 20m, MarkPrice = 3_000m, NotionalUsd = 6_000m, AveragePrice = 2_900m, UnrealizedProfitAndLoss = 200m };

        IReadOnlyList<VenuePosition> mapped = OkxMapper.Map(Now, Balance(), [shortSide, longSide]).Positions;

        mapped.Select(p => p.Quantity).Should().Equal(-5m, 2m);
    }

    [Fact]
    public void InverseSwap_ProfitInCoins_IsTurnedIntoUsd()
    {
        OKXPosition position = new() { Symbol = "BTC-USD-SWAP", InstrumentType = InstrumentType.Swap, PositionSide = PositionSide.Net, PositionsQuantity = 100m, MarkPrice = 80_000m, NotionalUsd = 10_000m, AveragePrice = 78_000m, UnrealizedProfitAndLoss = 0.003m };

        VenuePosition mapped = OkxMapper.Map(Now, Balance(), [position]).Positions.Single();

        mapped.Quantity.Should().Be(0.125m);
        mapped.UnsettledPnlUsd.Should().Be(240m);
    }

    [Fact]
    public void DatedFuture_CarriesItsExpiry()
    {
        OKXPosition position = new() { Symbol = "BTC-USDT-261225", InstrumentType = InstrumentType.Futures, PositionSide = PositionSide.Net, PositionsQuantity = 10m, MarkPrice = 81_000m, NotionalUsd = 8_100m, UnrealizedProfitAndLoss = 10m };

        VenuePosition mapped = OkxMapper.Map(Now, Balance(), [position]).Positions.Single();

        mapped.Type.Should().Be(FtInstrumentType.Future);
        mapped.Expiry.Should().Be(new DateOnly(2026, 12, 25));
        mapped.Quantity.Should().Be(0.1m);
    }

    [Fact]
    public void CoinSettledOption_PremiumAndDelta_ArePerUnitInUsd()
    {
        // Short 20 contracts of 0.01 BTC: 0.2 BTC of underlying, 16,000 USD notional at an 80,000 index.
        OKXPosition position = new() { Symbol = "BTC-USD-261225-90000-P", InstrumentType = InstrumentType.Option, PositionSide = PositionSide.Net, PositionsQuantity = -20m, MarkPrice = 0.05m, AveragePrice = 0.06m, IndexPrice = 80_000m, NotionalUsd = 16_000m, DeltaBS = 0.08m };

        VenuePosition mapped = OkxMapper.Map(Now, Balance(), [position]).Positions.Single();

        mapped.Should().Be(new VenuePosition(FtInstrumentType.Option, "BTC", -0.2m, 4_000m, 4_800m, null, new DateOnly(2026, 12, 25), 90_000m, FtOptionRight.Put, 80_000m, -0.4m));
    }

    [Fact]
    public void MarginLoans_AndEmptyPositions_AreLeftToTheBalances()
    {
        OKXPosition margin = new() { Symbol = "BTC-USDT", InstrumentType = InstrumentType.Margin, PositionsQuantity = 1m, MarkPrice = 80_000m, NotionalUsd = 80_000m };
        OKXPosition empty = new() { Symbol = "ETH-USDT-SWAP", InstrumentType = InstrumentType.Swap, PositionsQuantity = 0m };

        OkxMapper.Map(Now, Balance(), [margin, empty]).Positions.Should().BeEmpty();
    }

    [Fact]
    public void PositionWithoutAMarkPrice_FailsTheWholeSnapshot()
    {
        OKXPosition position = new() { Symbol = "BTC-USDT-SWAP", InstrumentType = InstrumentType.Swap, PositionsQuantity = 1m, NotionalUsd = 800m };

        FluentActions.Invoking(() => OkxMapper.Map(Now, Balance(), [position])).Should().Throw<VenueException>().WithMessage("OKX reported BTC-USDT-SWAP without a mark price.");
    }
}

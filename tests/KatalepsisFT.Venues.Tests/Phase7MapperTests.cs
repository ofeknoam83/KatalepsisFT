using System.Text.Json;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Coinbase;
using KatalepsisFT.Venues.Kucoin;
using KatalepsisFT.Venues.Mexc;
using Kucoin.Net.Objects.Models.Futures;
using Kucoin.Net.Objects.Models.Spot;
using Mexc.Net.Objects.Models.Futures;
using Mexc.Net.Objects.Models.Spot;
using MexcSide = Mexc.Net.Enums.PositionSide;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// KuCoin, MEXC and Coinbase. As with the other venues, the inputs are built by hand from the venues' documentation
/// and are not recordings of a real account.
/// </summary>
public class Phase7MapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Since = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ---------------------------------------------------------------- KuCoin

    [Theory]
    [InlineData("General", false, false)]
    [InlineData("General,Spot", true, false)]
    [InlineData("General,Futures,Withdrawal", true, true)]
    [InlineData("General,Transfer", false, true)]
    [InlineData("General,SomethingNew", true, false)]
    public void Kucoin_Permissions_OnlyGeneralIsReadOnly(string permissions, bool canTrade, bool canWithdraw)
    {
        KucoinMapper.Permissions(permissions).Should().Be(new CredentialCheck(canTrade, canWithdraw));
    }

    [Fact]
    public void Kucoin_PermissionsNotStated_AreUnverified()
    {
        KucoinMapper.Permissions(null).Verified.Should().BeFalse();
    }

    [Fact]
    public void Kucoin_AccountsAndFuturesWallets_AreOneLinePerAsset_AndXbtIsBtc()
    {
        KucoinAccount[] accounts = [new() { Asset = "BTC", Total = 1m }, new() { Asset = "BTC", Total = 0.5m }, new() { Asset = "USDT", Total = 2_000m }, new() { Asset = "KCS", Total = 0m }];
        KucoinAccountOverview[] futures = [new() { Asset = "USDT", MarginBalance = 3_000m, UnrealizedPnl = 777m }, new() { Asset = "XBT", MarginBalance = 0.25m }];

        VenueSnapshot snapshot = KucoinMapper.Map(Now, accounts, new Dictionary<string, decimal> { ["BTC"] = 80_000m }, futures, []);

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Spot, "BTC", 1.75m, 80_000m), new VenuePosition(InstrumentType.Cash, "USDT", 5_000m, 1m));
    }

    [Fact]
    public void Kucoin_LinearPerp_SizeInCoinsComesFromItsValue()
    {
        // 30 lots short worth 24,000 USDT at 80,000: 0.3 BTC.
        KucoinPosition position = new() { Symbol = "XBTUSDTM", CurrentQuantity = -30m, MarkPrice = 80_000m, MarkValue = -24_000m, IsInverse = false, UnrealizedPnl = 600m, AverageEntryPrice = 82_000m };

        VenuePosition mapped = KucoinMapper.Map(Now, [], new Dictionary<string, decimal>(), [], [position]).Positions.Single();

        mapped.Should().Be(new VenuePosition(InstrumentType.Perp, "BTC", -0.3m, 80_000m, 82_000m, 600m));
    }

    [Fact]
    public void Kucoin_InversePerp_ValueAndProfitAreInCoins()
    {
        KucoinPosition position = new() { Symbol = "XBTUSDM", CurrentQuantity = 10_000m, MarkPrice = 80_000m, MarkValue = -0.125m, IsInverse = true, UnrealizedPnl = 0.003m, AverageEntryPrice = 78_000m };

        VenuePosition mapped = KucoinMapper.Map(Now, [], new Dictionary<string, decimal>(), [], [position]).Positions.Single();

        mapped.Should().Be(new VenuePosition(InstrumentType.Perp, "BTC", 0.125m, 80_000m, 78_000m, 240m));
    }

    [Fact]
    public void Kucoin_HoldingWithoutAUsdPrice_OrAnUnreadableContract_FailsTheSnapshot()
    {
        FluentActions.Invoking(() => KucoinMapper.Map(Now, [new KucoinAccount { Asset = "OBSCURE", Total = 1m }], new Dictionary<string, decimal>(), [], [])).Should().Throw<VenueException>();
        FluentActions.Invoking(() => KucoinMapper.Map(Now, [], new Dictionary<string, decimal>(), [], [new KucoinPosition { Symbol = "XBTMH27", CurrentQuantity = 1m, MarkPrice = 1m }])).Should().Throw<VenueException>();
    }

    // ---------------------------------------------------------------- MEXC

    [Fact]
    public void Mexc_SpotAndFuturesWallets_AreOneLinePerAsset()
    {
        MexcAccountBalance[] spot = [new() { Asset = "ETH", Available = 2m, Locked = 1m }, new() { Asset = "USDT", Available = 500m }];
        MexcFuturesBalance[] futures = [new() { Asset = "USDT", CashBalance = 1_500m, Equity = 1_900m, UnrealizedPnl = 400m }];

        VenueSnapshot snapshot = MexcMapper.Map(Now, spot, new Dictionary<string, decimal> { ["ETHUSDT"] = 3_000m }, futures, [], new Dictionary<string, decimal>(), new Dictionary<string, decimal>());

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Spot, "ETH", 3m, 3_000m), new VenuePosition(InstrumentType.Cash, "USDT", 2_000m, 1m));
    }

    [Fact]
    public void Mexc_Position_IsContractsTimesContractSize_SignedBySide()
    {
        // 500 contracts of 0.0001 BTC short.
        MexcPosition position = new() { Symbol = "BTC_USDT", PositionSide = MexcSide.Short, PositionSize = 500m, HoldAveragePrice = 82_000m };

        VenuePosition mapped = MexcMapper.Map(Now, [], new Dictionary<string, decimal>(), [], [position], new Dictionary<string, decimal> { ["BTC_USDT"] = 0.0001m }, new Dictionary<string, decimal> { ["BTC_USDT"] = 80_000m }).Positions.Single();

        // No profit figure comes with the position, so it is left to be derived from the entry price.
        mapped.Should().Be(new VenuePosition(InstrumentType.Perp, "BTC", -0.05m, 80_000m, 82_000m));
    }

    [Fact]
    public void Mexc_PositionWithoutAContractSizeOrPrice_OrOfAnotherKind_FailsTheSnapshot()
    {
        MexcPosition position = new() { Symbol = "BTC_USDT", PositionSide = MexcSide.Long, PositionSize = 1m };
        Dictionary<string, decimal> size = new() { ["BTC_USDT"] = 0.0001m };
        Dictionary<string, decimal> none = [];

        FluentActions.Invoking(() => MexcMapper.Map(Now, [], none, [], [position], none, new Dictionary<string, decimal> { ["BTC_USDT"] = 1m })).Should().Throw<VenueException>();
        FluentActions.Invoking(() => MexcMapper.Map(Now, [], none, [], [position], size, none)).Should().Throw<VenueException>();
        FluentActions.Invoking(() => MexcMapper.Map(Now, [], none, [], [new MexcPosition { Symbol = "BTC_USD", PositionSize = 1m }], size, none)).Should().Throw<VenueException>();
    }

    // ---------------------------------------------------------------- Coinbase

    [Fact]
    public void CoinbaseExchange_Balances_ArePricedFromHowMuchADollarBuys()
    {
        JsonElement accounts = Json("""[{"id":"a1","currency":"BTC","balance":"1.5","hold":"0.5","available":"1.0"},{"id":"a2","currency":"USD","balance":"2000.00"},{"id":"a3","currency":"ETH","balance":"0"}]""");
        JsonElement rates = Json("""{"data":{"currency":"USD","rates":{"BTC":"0.0000125","ETH":"0.00033"}}}""");

        VenueSnapshot snapshot = CoinbaseMapper.ExchangeSnapshot(Now, accounts, rates);

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Spot, "BTC", 1.5m, 80_000m), new VenuePosition(InstrumentType.Cash, "USD", 2_000m, 1m));
    }

    [Fact]
    public void CoinbaseExchange_Transfers_CountWhenCompleted_SignedByType()
    {
        JsonElement accounts = Json("""[{"id":"a1","currency":"BTC","balance":"1"},{"id":"a2","currency":"USD","balance":"1"}]""");
        JsonElement transfers = Json("""
            [{"id":"t1","type":"deposit","account_id":"a2","amount":"5000.00","completed_at":"2026-11-21 08:00:00.000000+00"},
             {"id":"t2","type":"withdraw","account_id":"a1","amount":"0.25","completed_at":"2026-11-21 09:30:00.000000+00"},
             {"id":"t3","type":"internal_withdraw","currency":"USD","amount":"100","completed_at":"2026-11-21 10:00:00.000000+00"},
             {"id":"t4","type":"deposit","account_id":"a2","amount":"7","completed_at":null},
             {"id":"t5","type":"deposit","account_id":"a2","amount":"7","completed_at":"2026-11-21 08:00:00.000000+00","canceled_at":"2026-11-21 08:01:00.000000+00"},
             {"id":"t6","type":"deposit","account_id":"a2","amount":"7","completed_at":"2026-11-01 08:00:00.000000+00"}]
            """);

        IReadOnlyList<VenueTransfer> mapped = CoinbaseMapper.ExchangeTransfers(accounts, transfers, Since);

        mapped.Should().Equal(
            new VenueTransfer("t1", new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero), "USD", 5_000m),
            new VenueTransfer("t2", new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.Zero), "BTC", -0.25m),
            new VenueTransfer("t3", new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero), "USD", -100m)
        );
    }

    [Fact]
    public void CoinbasePrime_Portfolios_AreAddedUp_AtTheUsdValuePrimeStates()
    {
        JsonElement first = Json("""{"balances":[{"symbol":"btc","amount":"2","fiat_amount":"160000"},{"symbol":"usd","amount":"1000","fiat_amount":"1000"}]}""");
        JsonElement second = Json("""{"balances":[{"symbol":"btc","amount":"1","fiat_amount":"80000"},{"symbol":"eth","amount":"0","fiat_amount":"0"}]}""");

        VenueSnapshot snapshot = CoinbaseMapper.PrimeSnapshot(Now, [first, second]);

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Spot, "BTC", 3m, 80_000m), new VenuePosition(InstrumentType.Cash, "USD", 1_000m, 1m));
        snapshot.ReportedEquityUsd.Should().Be(241_000m);
    }

    [Fact]
    public void CoinbasePrime_Transactions_CountWhenDone_SignedByType()
    {
        JsonElement page = Json("""
            {"transactions":[
              {"id":"p1","type":"DEPOSIT","status":"TRANSACTION_DONE","symbol":"USD","amount":"25000","completed_at":"2026-11-21T08:00:00Z"},
              {"id":"p2","type":"WITHDRAWAL","status":"TRANSACTION_DONE","symbol":"BTC","amount":"-0.5","created_at":"2026-11-21T09:00:00Z"},
              {"id":"p3","type":"WITHDRAWAL","status":"TRANSACTION_PENDING","symbol":"BTC","amount":"9","created_at":"2026-11-21T09:00:00Z"},
              {"id":"p4","type":"CONVERSION","status":"TRANSACTION_DONE","symbol":"USDC","amount":"9","created_at":"2026-11-21T09:00:00Z"}]}
            """);

        CoinbaseMapper.PrimeTransfers(page, Since).Select(t => (t.Id, t.Asset, t.Quantity)).Should().Equal(("p1", "USD", 25_000m), ("p2", "BTC", -0.5m));
    }
}

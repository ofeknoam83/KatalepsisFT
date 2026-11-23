using System.Text.Json;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Rest;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// Kraken, Gate and Bitget. The inputs are written by hand in the shape each venue's documentation gives; they are
/// not recordings of a real account.
/// </summary>
public class Phase8MapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ---------------------------------------------------------------- Kraken

    [Theory]
    [InlineData("XXBT", "BTC")]
    [InlineData("XBT", "BTC")]
    [InlineData("XETH", "ETH")]
    [InlineData("ZUSD", "USD")]
    [InlineData("XXDG", "DOGE")]
    [InlineData("ETH2.S", "ETH")]
    [InlineData("DOT.F", "DOT")]
    [InlineData("USD.M", "USD")]
    [InlineData("ZETA", "ZETA")]
    [InlineData("USDT", "USDT")]
    [InlineData("SOL", "SOL")]
    public void Kraken_AssetCodes_BecomeOrdinaryTickers(string code, string expected)
    {
        KrakenMapper.Asset(code).Should().Be(expected);
    }

    [Fact]
    public void Kraken_Balances_AddStakedCoinsToTheCoin_AndArePricedFromTheLastTrade()
    {
        JsonElement balances = Json("""{"XXBT":"1.5","XBT.F":"0.5","ZUSD":"1000.00","USDT":"250","XETH":"0.0000000000","SOL":"10"}""");
        JsonElement tickers = Json("""{"XXBTZUSD":{"c":["80000.0","0.1"]},"XETHZUSD":{"c":["3000.0","1"]},"SOLUSD":{"c":["150.0","2"]}}""");

        VenueSnapshot snapshot = KrakenMapper.Map(Now, balances, tickers);

        snapshot.Positions.Should().Equal(
            new VenuePosition(InstrumentType.Spot, "BTC", 2m, 80_000m),
            new VenuePosition(InstrumentType.Spot, "SOL", 10m, 150m),
            new VenuePosition(InstrumentType.Cash, "USD", 1_000m, 1m),
            new VenuePosition(InstrumentType.Cash, "USDT", 250m, 1m)
        );
        snapshot.Notes.Should().ContainSingle().Which.Should().Contain("Kraken Futures");
    }

    [Fact]
    public void Kraken_HoldingWithoutAPrice_FailsTheSnapshot()
    {
        FluentActions.Invoking(() => KrakenMapper.Map(Now, Json("""{"ODD":"5"}"""), Json("{}"))).Should().Throw<VenueException>().WithMessage("Kraken holds ODD but lists no USD price for it.");
    }

    [Fact]
    public void Kraken_Ledger_TakesTheFeeOffDepositsAndWithdrawals()
    {
        JsonElement page = Json("""
            {"ledger":{
              "L1":{"refid":"r1","time":1795155907.5,"type":"deposit","asset":"ZUSD","amount":"5000.0000","fee":"5.0000","balance":"4995"},
              "L2":{"refid":"r2","time":1795155908,"type":"withdrawal","asset":"XXBT","amount":"-0.2500","fee":"0.0002","balance":"1"},
              "L3":{"refid":"r3","time":1795155909,"type":"trade","asset":"XXBT","amount":"9","fee":"0"}},
             "count":3}
            """);

        IReadOnlyList<VenueTransfer> transfers = KrakenMapper.Transfers(page);

        transfers.Select(t => (t.Id, t.Asset, t.Quantity)).Should().Equal(("L1", "USD", 4_995m), ("L2", "BTC", -0.2502m));
        transfers[0].At.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1795155907500));
        KrakenMapper.Transfers(Json("""{"count":0}""")).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Gate

    [Fact]
    public void Gate_SpotAndFuturesWallets_AreOneLine_AndPositionsComeFromTheirValue()
    {
        JsonElement spot = Json("""[{"currency":"USDT","available":"1000","locked":"500"},{"currency":"BTC","available":"0.5","locked":"0"},{"currency":"GT","available":"0","locked":"0"}]""");
        JsonElement tickers = Json("""[{"currency_pair":"BTC_USDT","last":"80000"},{"currency_pair":"BTC_USDC","last":"1"},{"currency_pair":"ETH_USDT","last":"3000"}]""");
        JsonElement futures = Json("""{"currency":"USDT","total":"2500","unrealised_pnl":"300","available":"2000"}""");
        // 300 contracts of 0.01 ETH short: 3 ETH, 9,000 USDT.
        JsonElement positions = Json("""[{"contract":"ETH_USDT","size":-300,"entry_price":"3100","mark_price":"3000","value":"9000","unrealised_pnl":"300"},{"contract":"BTC_USDT","size":0,"mark_price":"80000","value":"0"}]""");

        VenueSnapshot snapshot = GateMapper.Map(Now, spot, tickers, futures, positions);

        snapshot.Positions.Should().Equal(
            new VenuePosition(InstrumentType.Spot, "BTC", 0.5m, 80_000m),
            new VenuePosition(InstrumentType.Cash, "USDT", 4_000m, 1m),
            new VenuePosition(InstrumentType.Perp, "ETH", -3m, 3_000m, 3_100m, 300m)
        );
        ManagerBookWriter.NavOf(snapshot).Should().Be(40_000m + 4_000m + 300m);
    }

    [Fact]
    public void Gate_WithoutAFuturesAccount_IsSpotOnly()
    {
        VenueSnapshot snapshot = GateMapper.Map(Now, Json("""[{"currency":"USDT","available":"10","locked":"0"}]"""), Json("[]"), null, null);

        snapshot.Positions.Should().Equal(new VenuePosition(InstrumentType.Cash, "USDT", 10m, 1m));
    }

    [Fact]
    public void Gate_Transfers_CountWhenDone_WithdrawalsWithTheirFee()
    {
        JsonElement deposits = Json("""[{"id":"d1","timestamp":"1795155907","currency":"usdt","amount":"1000","status":"DONE"},{"id":"d2","timestamp":"1795155907","currency":"USDT","amount":"7","status":"PEND"}]""");
        JsonElement withdrawals = Json("""[{"id":"w1","timestamp":"1795155908","currency":"BTC","amount":"0.3","fee":"0.0005","status":"DONE"},{"id":"w2","timestamp":"1795155908","currency":"BTC","amount":"5","status":"CANCEL"}]""");

        GateMapper.Transfers(deposits, withdrawals).Should().Equal(
            new VenueTransfer("dep:d1", DateTimeOffset.FromUnixTimeSeconds(1795155907), "USDT", 1_000m),
            new VenueTransfer("wd:w1", DateTimeOffset.FromUnixTimeSeconds(1795155908), "BTC", -0.3005m)
        );
    }

    // ---------------------------------------------------------------- Bitget

    [Fact]
    public void Bitget_FuturesEquity_LosesItsOpenProfitToThePositions()
    {
        JsonElement spot = Json("""[{"coin":"USDT","available":"1000","frozen":"100","locked":"0"},{"coin":"ETH","available":"2","frozen":"0","locked":"1"}]""");
        JsonElement tickers = Json("""[{"symbol":"ETHUSDT","lastPr":"3000"},{"symbol":"BTCUSDT","lastPr":"80000"}]""");
        JsonElement accounts = Json("""[{"marginCoin":"USDT","accountEquity":"5600","unrealizedPL":"600","available":"4000"}]""");
        JsonElement positions = Json("""[{"symbol":"BTCUSDT","holdSide":"short","total":"0.3","openPriceAvg":"82000","markPrice":"80000","unrealizedPL":"600"},{"symbol":"ETHUSDT","holdSide":"long","total":"0","markPrice":"3000"}]""");

        VenueSnapshot snapshot = BitgetMapper.Map(Now, spot, tickers, accounts, positions);

        snapshot.Positions.Should().Equal(
            new VenuePosition(InstrumentType.Spot, "ETH", 3m, 3_000m),
            new VenuePosition(InstrumentType.Cash, "USDT", 6_100m, 1m),
            new VenuePosition(InstrumentType.Perp, "BTC", -0.3m, 80_000m, 82_000m, 600m)
        );
        // Spot 9,000 + cash 6,100 + open profit 600: the spot wallet's 10,100 plus the futures equity of 5,600.
        ManagerBookWriter.NavOf(snapshot).Should().Be(15_700m);
    }

    [Fact]
    public void Bitget_PositionWithoutAMarkPrice_OrWithAnUnreadableName_FailsTheSnapshot()
    {
        JsonElement none = Json("[]");

        FluentActions.Invoking(() => BitgetMapper.Map(Now, none, none, none, Json("""[{"symbol":"BTCUSDT","holdSide":"long","total":"1"}]"""))).Should().Throw<VenueException>().WithMessage("Bitget reported BTCUSDT without a mark price.");
        FluentActions.Invoking(() => BitgetMapper.Map(Now, none, none, none, Json("""[{"symbol":"BTCUSD","holdSide":"long","total":"1","markPrice":"1"}]"""))).Should().Throw<VenueException>();
    }

    [Fact]
    public void Bitget_Transfers_CountWhenSuccessful()
    {
        JsonElement deposits = Json("""[{"orderId":"1","coin":"USDT","size":"2500","status":"success","cTime":"1795155907000"},{"orderId":"2","coin":"USDT","size":"1","status":"pending","cTime":"1795155907000"}]""");
        JsonElement withdrawals = Json("""[{"orderId":"3","coin":"btc","size":"0.1","fee":"0.0002","status":"success","cTime":"1795155908000"}]""");

        BitgetMapper.Transfers(deposits, withdrawals).Select(t => (t.Id, t.Asset, t.Quantity)).Should().Equal(("dep:1", "USDT", 2_500m), ("wd:3", "BTC", -0.1002m));
        BitgetMapper.Transfers(Json("null"), Json("null")).Should().BeEmpty();
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Binance;
using KatalepsisFT.Venues.Bybit;
using KatalepsisFT.Venues.Deribit;
using KatalepsisFT.Venues.Okx;
using KatalepsisFT.Venues.Rest;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// What is sent to Binance, Bybit, OKX and Bitget for a real order, and how their answers are read: in the shapes
/// their own documentation gives. No request leaves the test.
/// </summary>
public class MoreOrderGatewayTests
{
    private static readonly VenueCredentials Key = new("key-1", "secret-1", "phrase-1");
    private static readonly OrderNames Names = new("kft42xa1b2c3d4e5f6", null);

    private sealed class Stub(Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Add((request, body));
            (HttpStatusCode status, string answer) = respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SetClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1_795_000_000_123);
    }

    private static (HttpClient Http, Stub Handler) Client(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond)
    {
        Stub handler = new(respond);
        return (new HttpClient(handler), handler);
    }

    private static VenueOrderRequest Order(InstrumentType type, OrderSide side, decimal quantity, decimal price, TimeInForce tif = TimeInForce.GoodTillCancelled, bool reduceOnly = false) =>
        new(Names.ClientId, type, side, OrderType.Limit, tif, quantity, price, reduceOnly, 3m, MarginMode.Cross);

    private static string Hex(string secret, string text) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Base64(string secret, string text) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text)));

    // ------------------------------------------------------------ Binance

    [Fact]
    public async Task Binance_SendsEverythingInTheAddress_SignedAsSent_AndSetsMarginAndLeverageBeforeAPerpetual()
    {
        (HttpClient http, Stub handler) = Client((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/marginType" => (HttpStatusCode.BadRequest, """{"code":-4046,"msg":"No need to change margin type."}"""),
            "/fapi/v1/leverage" => (HttpStatusCode.OK, """{"leverage":3,"maxNotionalValue":"1000000","symbol":"BTCUSDT"}"""),
            _ => (HttpStatusCode.OK, """{"symbol":"BTCUSDT","orderId":28,"clientOrderId":"kft42xa1b2c3d4e5f6","transactTime":1}"""),
        });
        BinanceOrderGateway gateway = new(http, new SetClock());
        VenueInstrument btc = new("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m);

        string? spotId = await gateway.PlaceAsync(Key, btc, Order(InstrumentType.Spot, OrderSide.Buy, 0.0125m, 61_000.5m, TimeInForce.PostOnly), CancellationToken.None);
        await gateway.PlaceAsync(Key, btc, Order(InstrumentType.Perp, OrderSide.Sell, 0.25m, 61_000m, TimeInForce.ImmediateOrCancel), CancellationToken.None);
        await gateway.PlaceAsync(Key, btc, Order(InstrumentType.Perp, OrderSide.Buy, 0.25m, 60_000m, TimeInForce.PostOnly, reduceOnly: true), CancellationToken.None);

        spotId.Should().Be("28");
        handler.Sent.Select(s => s.Request.RequestUri!.Host + s.Request.RequestUri.AbsolutePath).Should().Equal("api.binance.com/api/v3/order", "fapi.binance.com/fapi/v1/marginType", "fapi.binance.com/fapi/v1/leverage", "fapi.binance.com/fapi/v1/order", "fapi.binance.com/fapi/v1/order");
        string spot = handler.Sent[0].Request.RequestUri!.Query.TrimStart('?');
        // "Maker only" is its own order type on spot and takes no time in force.
        spot.Should().StartWith("symbol=BTCUSDT&side=BUY&type=LIMIT_MAKER&quantity=0.0125&price=61000.5&newClientOrderId=kft42xa1b2c3d4e5f6&newOrderRespType=ACK&recvWindow=5000&timestamp=1795000000123&signature=");
        spot.Should().EndWith("&signature=" + Hex("secret-1", spot[..spot.IndexOf("&signature=", StringComparison.Ordinal)]));
        handler.Sent[0].Request.Headers.GetValues("X-MBX-APIKEY").Single().Should().Be("key-1");
        handler.Sent[0].Body.Should().BeEmpty();
        handler.Sent[1].Request.RequestUri!.Query.Should().Contain("symbol=BTCUSDT&marginType=CROSSED");
        handler.Sent[2].Request.RequestUri!.Query.Should().Contain("symbol=BTCUSDT&leverage=3&");
        handler.Sent[3].Request.RequestUri!.Query.Should().Contain("side=SELL&type=LIMIT&timeInForce=IOC&quantity=0.25&price=61000&").And.NotContain("reduceOnly");
        handler.Sent[4].Request.RequestUri!.Query.Should().Contain("timeInForce=GTX").And.Contain("reduceOnly=true");
    }

    [Fact]
    public async Task Binance_ReadsAnOrderBack_WithItsFeesFromItsTrades_AndKnowsWhenThereIsNoSuchOrder()
    {
        (HttpClient http, Stub handler) = Client((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v3/order" when request.RequestUri.Query.Contains("kft1x", StringComparison.Ordinal) => (HttpStatusCode.BadRequest, """{"code":-2013,"msg":"Order does not exist."}"""),
            "/api/v3/order" => (HttpStatusCode.OK, """{"symbol":"BTCUSDT","orderId":28,"clientOrderId":"x","price":"61000","origQty":"0.02","executedQty":"0.015","cummulativeQuoteQty":"914.25","status":"CANCELED","timeInForce":"IOC","type":"LIMIT","side":"BUY"}"""),
            "/api/v3/myTrades" => (HttpStatusCode.OK, """[{"id":1,"orderId":28,"price":"60950","qty":"0.015","quoteQty":"914.25","commission":"0.000015","commissionAsset":"BTC"}]"""),
            "/fapi/v1/order" => (HttpStatusCode.OK, """{"avgPrice":"61006","clientOrderId":"x","cumQuote":"15251.5","executedQty":"0.25","orderId":77,"origQty":"0.3","status":"PARTIALLY_FILLED","symbol":"BTCUSDT"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        BinanceOrderGateway gateway = new(http, new SetClock());
        VenueInstrument btc = new("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m);

        VenueOrderReport spot = await gateway.FindAsync(Key, btc, InstrumentType.Spot, Names, CancellationToken.None);
        VenueOrderReport perp = await gateway.FindAsync(Key, btc, InstrumentType.Perp, Names, CancellationToken.None);
        VenueOrderReport none = await gateway.FindAsync(Key, btc, InstrumentType.Spot, new OrderNames("kft1x000000000000", null), CancellationToken.None);

        // The fee was taken in bitcoin: fifteen millionths of one, at the order's own price of 60,950.
        spot.Should().Be(new VenueOrderReport(VenueOrderState.Done, 0.015m, 914.25m, 0.91425m, "28"));
        perp.Should().Be(new VenueOrderReport(VenueOrderState.Open, 0.25m, 15_251.5m, null, "77"));
        none.State.Should().Be(VenueOrderState.NotFound);
        handler.Sent[0].Request.RequestUri!.Query.Should().Contain("symbol=BTCUSDT&origClientOrderId=kft42xa1b2c3d4e5f6");
        handler.Sent[1].Request.RequestUri!.Query.Should().Contain("symbol=BTCUSDT&orderId=28");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"code":-2010,"msg":"Account has insufficient balance for requested action."}""", true)]
    [InlineData(HttpStatusCode.Unauthorized, """{"code":-2015,"msg":"Invalid API-key, IP, or permissions for action."}""", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"code":-1008,"msg":"Server is currently overloaded with other requests."}""", false)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":-1007,"msg":"Timeout waiting for response from backend server. Send status unknown; execution status unknown."}""", false)]
    [InlineData((HttpStatusCode)418, """{"code":-1003,"msg":"Way too many requests; IP banned."}""", false)]
    [InlineData((HttpStatusCode)451, "<html>Unavailable For Legal Reasons</html>", false)]
    public async Task Binance_TellsAClearNoFromNoAnswerAtAll(HttpStatusCode status, string body, bool refused)
    {
        (HttpClient http, _) = Client((_, _) => (status, body));
        BinanceOrderGateway gateway = new(http, new SetClock());

        Func<Task> place = () => gateway.PlaceAsync(Key, new VenueInstrument("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m), Order(InstrumentType.Spot, OrderSide.Buy, 0.01m, 61_000m), CancellationToken.None);

        if (refused)
        {
            await place.Should().ThrowAsync<VenueRefusedException>();
        }
        else
        {
            await place.Should().ThrowAsync<VenueUnclearException>();
        }
    }

    [Fact]
    public async Task Binance_Rules_ComeFromItsFilters()
    {
        (HttpClient http, _) = Client((request, _) => request.RequestUri!.Host == "api.binance.com"
            ? (HttpStatusCode.OK, """{"symbols":[{"symbol":"BTCUSDT","status":"TRADING","filters":[{"filterType":"PRICE_FILTER","tickSize":"0.01000000"},{"filterType":"LOT_SIZE","minQty":"0.00001000","stepSize":"0.00001000"},{"filterType":"NOTIONAL","minNotional":"5.00000000","applyMinToMarket":true}]}]}""")
            : (HttpStatusCode.OK, """{"symbols":[{"symbol":"BTCUSDT","status":"TRADING","contractType":"PERPETUAL","quoteAsset":"USDT","filters":[{"filterType":"PRICE_FILTER","tickSize":"0.10"},{"filterType":"LOT_SIZE","minQty":"0.001","stepSize":"0.001"},{"filterType":"MIN_NOTIONAL","notional":"100"}]},{"symbol":"BTCUSDT_261225","status":"TRADING","contractType":"CURRENT_QUARTER","quoteAsset":"USDT","filters":[]}]}"""));
        BinanceOrderGateway gateway = new(http, new SetClock());

        (await gateway.InstrumentAsync("btc", InstrumentType.Spot, CancellationToken.None)).Should().Be(new VenueInstrument("BTCUSDT", 0.00001m, 0.01m, 0.00001m, 5m));
        (await gateway.InstrumentAsync("BTC", InstrumentType.Perp, CancellationToken.None)).Should().Be(new VenueInstrument("BTCUSDT", 0.001m, 0.1m, 0.001m, 100m));
        (await gateway.InstrumentAsync("NOSUCH", InstrumentType.Perp, CancellationToken.None)).Should().BeNull();
    }

    // ------------------------------------------------------------ Bybit

    [Fact]
    public async Task Bybit_SignsTheBodyItSends_AndSaysWhichMarketByCategory()
    {
        (HttpClient http, Stub handler) = Client((_, _) => (HttpStatusCode.OK, """{"retCode":0,"retMsg":"OK","result":{"orderId":"1321","orderLinkId":"kft42xa1b2c3d4e5f6"},"retExtInfo":{},"time":1}"""));
        BybitOrderGateway gateway = new(http, new SetClock());
        VenueInstrument btc = new("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m);

        string? id = await gateway.PlaceAsync(Key, btc, Order(InstrumentType.Spot, OrderSide.Buy, 0.0125m, 61_000.5m, TimeInForce.PostOnly), CancellationToken.None);
        await gateway.PlaceAsync(Key, btc, Order(InstrumentType.Perp, OrderSide.Sell, 0.25m, 61_000m, TimeInForce.ImmediateOrCancel), CancellationToken.None);

        id.Should().Be("1321");
        (HttpRequestMessage request, string body) = handler.Sent[0];
        using JsonDocument spot = JsonDocument.Parse(body);
        request.RequestUri!.AbsoluteUri.Should().Be("https://api.bybit.com/v5/order/create");
        spot.RootElement.GetProperty("category").GetString().Should().Be("spot");
        spot.RootElement.GetProperty("side").GetString().Should().Be("Buy");
        spot.RootElement.GetProperty("orderType").GetString().Should().Be("Limit");
        spot.RootElement.GetProperty("qty").GetString().Should().Be("0.0125");
        spot.RootElement.GetProperty("price").GetString().Should().Be("61000.5");
        spot.RootElement.GetProperty("timeInForce").GetString().Should().Be("PostOnly");
        spot.RootElement.GetProperty("orderLinkId").GetString().Should().Be("kft42xa1b2c3d4e5f6");
        spot.RootElement.TryGetProperty("positionIdx", out _).Should().BeFalse();
        request.Headers.GetValues("X-BAPI-SIGN").Single().Should().Be(Hex("secret-1", "1795000000123key-15000" + body));
        request.Headers.GetValues("X-BAPI-TIMESTAMP").Single().Should().Be("1795000000123");
        // The perpetual: leverage first, then the order in one-way mode.
        handler.Sent[1].Request.RequestUri!.AbsolutePath.Should().Be("/v5/position/set-leverage");
        handler.Sent[1].Body.Should().Be("""{"category":"linear","symbol":"BTCUSDT","buyLeverage":"3","sellLeverage":"3"}""");
        using JsonDocument perp = JsonDocument.Parse(handler.Sent[2].Body);
        perp.RootElement.GetProperty("category").GetString().Should().Be("linear");
        perp.RootElement.GetProperty("positionIdx").GetInt32().Should().Be(0);
        perp.RootElement.GetProperty("timeInForce").GetString().Should().Be("IOC");
    }

    [Fact]
    public async Task Bybit_LooksInTheHistoryWhenTheLiveListIsEmpty_AndOnlyThenSaysNotFound()
    {
        (HttpClient http, Stub handler) = Client((request, _) =>
            request.RequestUri!.AbsolutePath == "/v5/order/realtime" || request.RequestUri.Query.Contains("kft1x", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"retCode":0,"retMsg":"OK","result":{"list":[],"nextPageCursor":""}}""")
                : (HttpStatusCode.OK, """{"retCode":0,"retMsg":"OK","result":{"list":[{"orderId":"1321","orderLinkId":"x","symbol":"BTCUSDT","orderStatus":"PartiallyFilledCanceled","avgPrice":"60950","cumExecQty":"0.015","cumExecValue":"914.25","cumExecFee":"0","cumFeeDetail":{"BTC":"0.000015"},"rejectReason":"EC_NoError"}]}}"""));
        BybitOrderGateway gateway = new(http, new SetClock());
        VenueInstrument btc = new("BTCUSDT", 0.000001m, 0.1m, 0.000048m, 5m);

        VenueOrderReport found = await gateway.FindAsync(Key, btc, InstrumentType.Spot, Names, CancellationToken.None);
        VenueOrderReport none = await gateway.FindAsync(Key, btc, InstrumentType.Spot, new OrderNames("kft1x000000000000", null), CancellationToken.None);

        found.Should().Be(new VenueOrderReport(VenueOrderState.Done, 0.015m, 914.25m, 0.91425m, "1321"));
        none.State.Should().Be(VenueOrderState.NotFound);
        handler.Sent.Select(s => s.Request.RequestUri!.AbsolutePath).Should().Equal("/v5/order/realtime", "/v5/order/history", "/v5/order/realtime", "/v5/order/history");
        string query = handler.Sent[0].Request.RequestUri!.Query.TrimStart('?');
        query.Should().Be("category=spot&symbol=BTCUSDT&orderLinkId=kft42xa1b2c3d4e5f6");
        // A GET signs its query, not a body, and carries none.
        handler.Sent[0].Request.Headers.GetValues("X-BAPI-SIGN").Single().Should().Be(Hex("secret-1", "1795000000123key-15000" + query));
        handler.Sent[0].Body.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"retCode":110007,"retMsg":"Insufficient available balance","result":{}}""", true)]
    [InlineData(HttpStatusCode.OK, """{"retCode":10003,"retMsg":"API key is invalid.","result":{}}""", true)]
    [InlineData(HttpStatusCode.OK, """{"retCode":10016,"retMsg":"Server error.","result":{}}""", false)]
    [InlineData(HttpStatusCode.Forbidden, "", false)]
    public async Task Bybit_TellsAClearNoFromNoAnswerAtAll(HttpStatusCode status, string body, bool refused)
    {
        (HttpClient http, _) = Client((_, _) => (status, body));
        BybitOrderGateway gateway = new(http, new SetClock());

        Func<Task> cancel = () => gateway.CancelAsync(Key, new VenueInstrument("BTCUSDT", 0.001m, 0.1m, 0.001m, 5m), InstrumentType.Spot, Names, CancellationToken.None);

        if (refused)
        {
            await cancel.Should().ThrowAsync<VenueRefusedException>();
        }
        else
        {
            await cancel.Should().ThrowAsync<VenueUnclearException>();
        }
    }

    // ------------------------------------------------------------ OKX

    private const string OkxSwap = """{"code":"0","msg":"","data":[{"instType":"SWAP","instId":"BTC-USDT-SWAP","ctVal":"0.01","lotSz":"0.01","minSz":"0.01","tickSz":"0.1","state":"live"}]}""";
    private const string OkxSpot = """{"code":"0","msg":"","data":[{"instType":"SPOT","instId":"BTC-USDT","ctVal":"","lotSz":"0.00000001","minSz":"0.00001","tickSz":"0.1","state":"live"}]}""";

    [Fact]
    public async Task Okx_TradesSwapsInContracts_AndReadsThemBackInUnitsOfTheAsset()
    {
        (HttpClient http, Stub handler) = Client((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v5/public/instruments" => (HttpStatusCode.OK, request.RequestUri.Query.Contains("SWAP", StringComparison.Ordinal) ? OkxSwap : OkxSpot),
            "/api/v5/account/leverage-info" => (HttpStatusCode.OK, """{"code":"0","msg":"","data":[{"instId":"BTC-USDT-SWAP","lever":"10","mgnMode":"cross"}]}"""),
            "/api/v5/account/set-leverage" => (HttpStatusCode.OK, """{"code":"0","msg":"","data":[{"lever":"3","mgnMode":"cross","instId":"BTC-USDT-SWAP","posSide":""}]}"""),
            "/api/v5/trade/order" when request.Method == HttpMethod.Post => (HttpStatusCode.OK, """{"code":"0","msg":"","data":[{"clOrdId":"kft42xa1b2c3d4e5f6","ordId":"3122","tag":"","sCode":"0","sMsg":""}]}"""),
            _ => (HttpStatusCode.OK, """{"code":"0","msg":"","data":[{"instId":"BTC-USDT-SWAP","ordId":"3122","clOrdId":"x","state":"partially_filled","accFillSz":"25","avgPx":"61006","fee":"-9.15","feeCcy":"USDT","sz":"30"}]}"""),
        });
        OkxOrderGateway gateway = new(http, new SetClock());
        VenueInstrument swap = (await gateway.InstrumentAsync("BTC", InstrumentType.Perp, CancellationToken.None))!;
        VenueInstrument spot = (await gateway.InstrumentAsync("BTC", InstrumentType.Spot, CancellationToken.None))!;

        string? id = await gateway.PlaceAsync(Key, swap, Order(InstrumentType.Perp, OrderSide.Sell, 0.25m, 61_000m, TimeInForce.ImmediateOrCancel), CancellationToken.None);
        VenueOrderReport report = await gateway.FindAsync(Key, swap, InstrumentType.Perp, Names, CancellationToken.None);

        // One contract is a hundredth of a bitcoin, and orders move in hundredths of a contract.
        swap.Should().Be(new VenueInstrument("BTC-USDT-SWAP", 0.0001m, 0.1m, 0.0001m, 0m));
        spot.Should().Be(new VenueInstrument("BTC-USDT", 0.00000001m, 0.1m, 0.00001m, 0m));
        id.Should().Be("3122");
        (HttpRequestMessage placed, string body) = handler.Sent.Single(s => s.Request.Method == HttpMethod.Post && s.Request.RequestUri!.AbsolutePath == "/api/v5/trade/order");
        body.Should().Be("""{"instId":"BTC-USDT-SWAP","tdMode":"cross","clOrdId":"kft42xa1b2c3d4e5f6","side":"sell","ordType":"ioc","px":"61000","sz":"25"}""");
        placed.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single().Should().Be("2026-11-18T11:06:40.123Z");
        placed.Headers.GetValues("OK-ACCESS-SIGN").Single().Should().Be(Base64("secret-1", "2026-11-18T11:06:40.123ZPOST/api/v5/trade/order" + body));
        placed.Headers.GetValues("OK-ACCESS-PASSPHRASE").Single().Should().Be("phrase-1");
        // Leverage was ten; it is asked first and then set to ours.
        handler.Sent.Select(s => s.Request.RequestUri!.AbsolutePath).Should().ContainInOrder("/api/v5/account/leverage-info", "/api/v5/account/set-leverage", "/api/v5/trade/order");
        report.Should().Be(new VenueOrderReport(VenueOrderState.Open, 0.25m, 15_251.5m, 9.15m, "3122"));
        handler.Sent[^1].Request.Headers.GetValues("OK-ACCESS-SIGN").Single().Should().Be(Base64("secret-1", "2026-11-18T11:06:40.123ZGET/api/v5/trade/order?instId=BTC-USDT-SWAP&clOrdId=kft42xa1b2c3d4e5f6"));
    }

    [Theory]
    [InlineData("""{"code":"1","msg":"Operation failed.","data":[{"clOrdId":"x","ordId":"","sCode":"51008","sMsg":"Order failed. Insufficient USDT balance in account."}]}""", "Insufficient USDT balance", true)]
    [InlineData("""{"code":"0","msg":"","data":[{"clOrdId":"x","ordId":"","sCode":"51000","sMsg":"Parameter posSide error"}]}""", "posSide", true)]
    [InlineData("""{"code":"50113","msg":"Invalid Sign","data":[]}""", "Invalid Sign", true)]
    [InlineData("""{"code":"1","msg":"","data":[{"clOrdId":"x","ordId":"","sCode":"51149","sMsg":"Order timed out. Please try again later."}]}""", "timed out", false)]
    [InlineData("""{"code":"50013","msg":"Systems are busy. Please try again later.","data":[]}""", "busy", false)]
    public async Task Okx_ReadsTheVerdictOnTheOrderItself_EvenWhenTheRequestIsMarkedDone(string body, string says, bool refused)
    {
        (HttpClient http, _) = Client((request, _) => request.RequestUri!.AbsolutePath == "/api/v5/public/instruments" ? (HttpStatusCode.OK, OkxSpot) : (HttpStatusCode.OK, body));
        OkxOrderGateway gateway = new(http, new SetClock());
        VenueInstrument spot = (await gateway.InstrumentAsync("BTC", InstrumentType.Spot, CancellationToken.None))!;

        Func<Task> place = () => gateway.PlaceAsync(Key, spot, Order(InstrumentType.Spot, OrderSide.Buy, 0.01m, 61_000m), CancellationToken.None);

        if (refused)
        {
            (await place.Should().ThrowAsync<VenueRefusedException>()).Which.Message.Should().Contain(says);
        }
        else
        {
            (await place.Should().ThrowAsync<VenueUnclearException>()).Which.Message.Should().Contain(says);
        }
    }

    [Fact]
    public async Task Okx_SpotBuyPaysItsFeeInTheCoin_AndAnUnknownOrderIsNotFound()
    {
        (HttpClient http, _) = Client((request, _) => request.RequestUri!.AbsolutePath == "/api/v5/public/instruments" ? (HttpStatusCode.OK, OkxSpot)
            : request.RequestUri.Query.Contains("kft1x", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"code":"51603","msg":"Order does not exist.","data":[]}""")
            : (HttpStatusCode.OK, """{"code":"0","msg":"","data":[{"instId":"BTC-USDT","ordId":"9","state":"filled","accFillSz":"0.015","avgPx":"60950","fee":"-0.000015","feeCcy":"BTC"}]}"""));
        OkxOrderGateway gateway = new(http, new SetClock());
        VenueInstrument spot = (await gateway.InstrumentAsync("BTC", InstrumentType.Spot, CancellationToken.None))!;

        (await gateway.FindAsync(Key, spot, InstrumentType.Spot, Names, CancellationToken.None)).Should().Be(new VenueOrderReport(VenueOrderState.Done, 0.015m, 914.25m, 0.91425m, "9"));
        (await gateway.FindAsync(Key, spot, InstrumentType.Spot, new OrderNames("kft1x000000000000", null), CancellationToken.None)).State.Should().Be(VenueOrderState.NotFound);
    }

    // ------------------------------------------------------------ Bitget

    [Fact]
    public async Task Bitget_SendsSpotAndPerpetualsToTheirOwnAddresses_SignedAsSent()
    {
        (HttpClient http, Stub handler) = Client((_, _) => (HttpStatusCode.OK, """{"code":"00000","msg":"success","requestTime":1,"data":{"orderId":"1001","clientOid":"kft42xa1b2c3d4e5f6"}}"""));
        BitgetOrderGateway gateway = new(http, new SetClock());
        VenueInstrument btc = new("BTCUSDT", 0.0001m, 0.1m, 0.0001m, 5m);

        string? id = await gateway.PlaceAsync(Key, btc, Order(InstrumentType.Spot, OrderSide.Buy, 0.0125m, 61_000.5m, TimeInForce.PostOnly), CancellationToken.None);
        await gateway.PlaceAsync(Key, btc, Order(InstrumentType.Perp, OrderSide.Sell, 0.25m, 61_000m, TimeInForce.ImmediateOrCancel), CancellationToken.None);
        await gateway.PlaceAsync(Key, btc, Order(InstrumentType.Perp, OrderSide.Buy, 0.25m, 60_000m, reduceOnly: true), CancellationToken.None);

        id.Should().Be("1001");
        handler.Sent.Select(s => s.Request.RequestUri!.AbsolutePath).Should().Equal("/api/v2/spot/trade/place-order", "/api/v2/mix/account/set-leverage", "/api/v2/mix/order/place-order", "/api/v2/mix/order/place-order");
        handler.Sent[0].Body.Should().Be("""{"symbol":"BTCUSDT","side":"buy","orderType":"limit","force":"post_only","price":"61000.5","size":"0.0125","clientOid":"kft42xa1b2c3d4e5f6"}""");
        handler.Sent[0].Request.Headers.GetValues("ACCESS-SIGN").Single().Should().Be(Base64("secret-1", "1795000000123POST/api/v2/spot/trade/place-order" + handler.Sent[0].Body));
        handler.Sent[0].Request.Headers.GetValues("ACCESS-PASSPHRASE").Single().Should().Be("phrase-1");
        handler.Sent[1].Body.Should().Be("""{"symbol":"BTCUSDT","productType":"USDT-FUTURES","marginCoin":"USDT","leverage":"3"}""");
        using JsonDocument perp = JsonDocument.Parse(handler.Sent[2].Body);
        perp.RootElement.GetProperty("productType").GetString().Should().Be("USDT-FUTURES");
        perp.RootElement.GetProperty("marginMode").GetString().Should().Be("crossed");
        perp.RootElement.GetProperty("marginCoin").GetString().Should().Be("USDT");
        perp.RootElement.GetProperty("force").GetString().Should().Be("ioc");
        perp.RootElement.GetProperty("size").GetString().Should().Be("0.25");
        perp.RootElement.TryGetProperty("reduceOnly", out _).Should().BeFalse();
        using JsonDocument closing = JsonDocument.Parse(handler.Sent[3].Body);
        closing.RootElement.GetProperty("reduceOnly").GetString().Should().Be("YES");
    }

    [Fact]
    public async Task Bitget_ReadsItsTwoShapesOfAnswer_AndItsTwoSpellingsOfCancelled()
    {
        (HttpClient http, Stub handler) = Client((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/spot/trade/orderInfo" when request.RequestUri.Query.Contains("kft1x", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"code":"00000","msg":"success","data":[]}"""),
            "/api/v2/spot/trade/orderInfo" => (HttpStatusCode.OK, """{"code":"00000","msg":"success","data":[{"orderId":"1001","clientOid":"x","symbol":"BTCUSDT","side":"buy","status":"cancelled","baseVolume":"0.015","quoteVolume":"914.25","priceAvg":"60950","feeDetail":"{\"newFees\":{\"c\":0,\"d\":0,\"deduction\":false,\"r\":-0.000015,\"t\":-0.000015,\"totalDeductionFee\":0}}"}]}"""),
            _ when request.RequestUri.Query.Contains("kft1x", StringComparison.Ordinal) => (HttpStatusCode.BadRequest, """{"code":"40109","msg":"The data of the order cannot be found, please confirm the order number","data":null}"""),
            _ => (HttpStatusCode.OK, """{"code":"00000","msg":"success","data":{"orderId":"2002","symbol":"btcusdt","state":"canceled","baseVolume":"0.25","quoteVolume":"15251.5","fee":"-9.15","side":"sell","marginMode":"cross"}}"""),
        });
        BitgetOrderGateway gateway = new(http, new SetClock());
        VenueInstrument btc = new("BTCUSDT", 0.0001m, 0.1m, 0.0001m, 5m);
        OrderNames unknown = new("kft1x000000000000", null);

        (await gateway.FindAsync(Key, btc, InstrumentType.Spot, Names, CancellationToken.None)).Should().Be(new VenueOrderReport(VenueOrderState.Done, 0.015m, 914.25m, 0.91425m, "1001"));
        (await gateway.FindAsync(Key, btc, InstrumentType.Perp, Names, CancellationToken.None)).Should().Be(new VenueOrderReport(VenueOrderState.Done, 0.25m, 15_251.5m, 9.15m, "2002"));
        (await gateway.FindAsync(Key, btc, InstrumentType.Spot, unknown, CancellationToken.None)).State.Should().Be(VenueOrderState.NotFound);
        (await gateway.FindAsync(Key, btc, InstrumentType.Perp, unknown, CancellationToken.None)).State.Should().Be(VenueOrderState.NotFound);
        handler.Sent[1].Request.RequestUri!.PathAndQuery.Should().Be("/api/v2/mix/order/detail?symbol=BTCUSDT&productType=USDT-FUTURES&clientOid=kft42xa1b2c3d4e5f6");
        handler.Sent[1].Request.Headers.GetValues("ACCESS-SIGN").Single().Should().Be(Base64("secret-1", "1795000000123GET/api/v2/mix/order/detail?symbol=BTCUSDT&productType=USDT-FUTURES&clientOid=kft42xa1b2c3d4e5f6"));
    }

    [Fact]
    public void Bitget_KeyPermissions_AreReadFromItsCodes()
    {
        static CredentialCheck Check(string authorities) => BitgetAdapter.Permissions(JsonDocument.Parse($$"""{"userId":"1","ips":"","authorities":[{{authorities}}]}""").RootElement);

        Check("\"stor\",\"coor\",\"cpor\"").Should().Be(new CredentialCheck(false, false));
        Check("\"stor\",\"stow\",\"coow\",\"cpow\"").Should().Be(new CredentialCheck(true, false));
        Check("\"stow\",\"wwow\"").Should().Be(new CredentialCheck(true, true));
        Check("\"stor\",\"wtow\"").Should().Be(new CredentialCheck(false, true));
        // A code that is not known is taken to allow trading, never to be harmless.
        Check("\"stor\",\"newthing\"").Should().Be(new CredentialCheck(true, false));
        BitgetAdapter.Permissions(JsonDocument.Parse("""{"userId":"1"}""").RootElement).Should().Be(new CredentialCheck(null, null));
    }

    // ------------------------------------------------------------ Deribit

    [Fact]
    public async Task Deribit_PutsTheSideInTheCall_AndAsksByItsOwnIdOnceItIsKnown()
    {
        (HttpClient http, Stub handler) = Client((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/private/sell" => (HttpStatusCode.OK, """{"jsonrpc":"2.0","result":{"trades":[],"order":{"order_id":"USDC-584","order_state":"open","label":"kft42xa1b2c3d4e5f6","filled_amount":0,"amount":0.25}}}"""),
            "/api/v2/private/get_order_state" => (HttpStatusCode.OK, """{"jsonrpc":"2.0","result":{"order_id":"USDC-584","order_state":"cancelled","label":"kft42xa1b2c3d4e5f6","filled_amount":0.1,"average_price":61006.0,"amount":0.25,"cancel_reason":"user_request"}}"""),
            "/api/v2/private/get_user_trades_by_order" => (HttpStatusCode.OK, """{"jsonrpc":"2.0","result":[{"trade_id":"1","order_id":"USDC-584","price":61006.0,"amount":0.1,"fee":2.13521,"fee_currency":"USDC","liquidity":"T"}]}"""),
            "/api/v2/private/get_order_state_by_label" when request.RequestUri.Query.Contains("kft1x", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"jsonrpc":"2.0","result":[]}"""),
            _ => (HttpStatusCode.OK, """{"jsonrpc":"2.0","result":[{"order_id":"USDC-584","order_state":"open","label":"kft42xa1b2c3d4e5f6","filled_amount":0.05,"average_price":61000.0,"creation_timestamp":2},{"order_id":"USDC-1","order_state":"open","label":"someone-elses","filled_amount":9,"average_price":1,"creation_timestamp":3}]}"""),
        });
        DeribitOrderGateway gateway = new(http, new SetClock());
        VenueInstrument btc = new("BTC_USDC-PERPETUAL", 0.0001m, 0.1m, 0.0001m, 0m);

        string? id = await gateway.PlaceAsync(Key, btc, Order(InstrumentType.Perp, OrderSide.Sell, 0.25m, 61_000.5m, TimeInForce.PostOnly, reduceOnly: true), CancellationToken.None);
        VenueOrderReport byLabel = await gateway.FindAsync(Key, btc, InstrumentType.Perp, Names, CancellationToken.None);
        VenueOrderReport byId = await gateway.FindAsync(Key, btc, InstrumentType.Perp, Names with { VenueId = id }, CancellationToken.None);
        VenueOrderReport none = await gateway.FindAsync(Key, btc, InstrumentType.Perp, new OrderNames("kft1x000000000000", null), CancellationToken.None);
        await gateway.CancelAsync(Key, btc, InstrumentType.Perp, Names with { VenueId = id }, CancellationToken.None);
        await gateway.CancelAsync(Key, btc, InstrumentType.Perp, Names, CancellationToken.None);

        id.Should().Be("USDC-584");
        handler.Sent[0].Request.RequestUri!.PathAndQuery.Should().Be("/api/v2/private/sell?instrument_name=BTC_USDC-PERPETUAL&amount=0.25&type=limit&price=61000.5&label=kft42xa1b2c3d4e5f6&time_in_force=good_til_cancelled&post_only=true&reject_post_only=true&reduce_only=true");
        handler.Sent[0].Request.Headers.Authorization!.ToString().Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("key-1:secret-1")));
        byLabel.Should().Be(new VenueOrderReport(VenueOrderState.Open, 0.05m, 3_050m, null, "USDC-584"));
        byId.Should().Be(new VenueOrderReport(VenueOrderState.Done, 0.1m, 6_100.6m, 2.13521m, "USDC-584"));
        none.State.Should().Be(VenueOrderState.NotFound);
        handler.Sent[^2].Request.RequestUri!.PathAndQuery.Should().Be("/api/v2/private/cancel?order_id=USDC-584");
        handler.Sent[^1].Request.RequestUri!.PathAndQuery.Should().Be("/api/v2/private/cancel_by_label?label=kft42xa1b2c3d4e5f6&currency=USDC");
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","error":{"code":10009,"message":"not_enough_funds"}}""", true)]
    [InlineData("""{"jsonrpc":"2.0","error":{"code":13009,"message":"unauthorized","data":{"reason":"bad_ip"}}}""", true)]
    [InlineData("""{"jsonrpc":"2.0","error":{"code":11054,"message":"post_only_reject"}}""", true)]
    [InlineData("""{"jsonrpc":"2.0","error":{"code":13028,"message":"temporarily_unavailable"}}""", false)]
    [InlineData("""{"jsonrpc":"2.0","error":{"code":10047,"message":"matching_engine_queue_full"}}""", false)]
    public async Task Deribit_TellsAClearNoFromNoAnswerAtAll(string body, bool refused)
    {
        (HttpClient http, _) = Client((_, _) => (HttpStatusCode.BadRequest, body));
        DeribitOrderGateway gateway = new(http, new SetClock());

        Func<Task> place = () => gateway.PlaceAsync(Key, new VenueInstrument("BTC_USDC-PERPETUAL", 0.0001m, 0.1m, 0.0001m, 0m), Order(InstrumentType.Perp, OrderSide.Buy, 0.01m, 61_000m), CancellationToken.None);

        if (refused)
        {
            await place.Should().ThrowAsync<VenueRefusedException>();
        }
        else
        {
            await place.Should().ThrowAsync<VenueUnclearException>();
        }
    }
}

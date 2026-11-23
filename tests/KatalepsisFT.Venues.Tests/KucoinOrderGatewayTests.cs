using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Kucoin;

namespace KatalepsisFT.Venues.Tests;

/// <summary>What is sent to KuCoin for a real order, and how each of its answers is read. No request leaves the test.</summary>
public class KucoinOrderGatewayTests
{
    private static readonly VenueCredentials Key = new("key-1", "secret-1", "phrase-1");
    private static readonly VenueInstrument BtcSpot = new("BTC-USDT", 0.00000001m, 0.1m, 0.00001m, 0.1m);
    private static readonly VenueInstrument BtcPerp = new("XBTUSDTM", 0.001m, 0.1m, 0.001m, 0m);

    private const string Contracts = """{"code":"200000","data":[{"symbol":"XBTUSDTM","baseCurrency":"XBT","quoteCurrency":"USDT","multiplier":0.001,"lotSize":1,"tickSize":0.1,"status":"Open","expireDate":null},{"symbol":"DOGEUSDTM","baseCurrency":"DOGE","quoteCurrency":"USDT","multiplier":100.0,"lotSize":1,"tickSize":0.00001,"status":"Open","expireDate":null},{"symbol":"XBTUSDM","baseCurrency":"XBT","quoteCurrency":"USD","multiplier":-1,"lotSize":1,"tickSize":0.1,"status":"Open","expireDate":null}]}""";

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
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1_795_000_000_000);
    }

    private static (KucoinOrderGateway Gateway, Stub Handler) Gateway(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond)
    {
        Stub handler = new(respond);
        return (new KucoinOrderGateway(new HttpClient(handler), new SetClock()), handler);
    }

    private static string Ok(string data) => $$"""{"code":"200000","data":{{data}}}""";

    private static VenueOrderRequest Order(InstrumentType type, OrderSide side, decimal quantity, decimal price, TimeInForce tif = TimeInForce.GoodTillCancelled, bool reduceOnly = false, MarginMode margin = MarginMode.Cross) =>
        new("kft42-a1b2c3d4e5f6", type, side, OrderType.Limit, tif, quantity, price, reduceOnly, 3m, margin);

    [Fact]
    public async Task SpotOrder_GoesOutWithOurNameForIt_ALatestArrival_AndASignatureOverExactlyWhatIsSent()
    {
        (KucoinOrderGateway gateway, Stub handler) = Gateway((_, _) => (HttpStatusCode.OK, Ok("""{"orderId":"67abc","clientOid":"kft42-a1b2c3d4e5f6"}""")));

        string? id = await gateway.PlaceAsync(Key, BtcSpot, Order(InstrumentType.Spot, OrderSide.Buy, 0.0125m, 61_000.50m, TimeInForce.ImmediateOrCancel), CancellationToken.None);

        (HttpRequestMessage request, string body) = handler.Sent.Single();
        using JsonDocument sent = JsonDocument.Parse(body);
        id.Should().Be("67abc");
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsoluteUri.Should().Be("https://api.kucoin.com/api/v1/hf/orders");
        sent.RootElement.GetProperty("clientOid").GetString().Should().Be("kft42-a1b2c3d4e5f6");
        sent.RootElement.GetProperty("symbol").GetString().Should().Be("BTC-USDT");
        sent.RootElement.GetProperty("side").GetString().Should().Be("buy");
        sent.RootElement.GetProperty("type").GetString().Should().Be("limit");
        sent.RootElement.GetProperty("price").GetString().Should().Be("61000.5");
        sent.RootElement.GetProperty("size").GetString().Should().Be("0.0125");
        sent.RootElement.GetProperty("timeInForce").GetString().Should().Be("IOC");
        sent.RootElement.GetProperty("allowMaxTimeWindow").GetInt32().Should().Be(5000);
        sent.RootElement.GetProperty("clientTimestamp").GetInt64().Should().Be(1_795_000_000_000);
        sent.RootElement.TryGetProperty("postOnly", out _).Should().BeFalse();
        string expected = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret-1"), Encoding.UTF8.GetBytes("1795000000000POST/api/v1/hf/orders" + body)));
        request.Headers.GetValues("KC-API-SIGN").Single().Should().Be(expected);
        request.Headers.GetValues("KC-API-KEY").Single().Should().Be("key-1");
        request.Headers.GetValues("KC-API-TIMESTAMP").Single().Should().Be("1795000000000");
        // The passphrase never travels as it is.
        request.Headers.GetValues("KC-API-PASSPHRASE").Single().Should().Be(Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret-1"), Encoding.UTF8.GetBytes("phrase-1"))));
    }

    [Fact]
    public async Task PerpOrder_IsSentInWholeContracts_AfterLeverageHasBeenSetToOurs()
    {
        (KucoinOrderGateway gateway, Stub handler) = Gateway((request, _) => (HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("changeCrossUserLeverage", StringComparison.Ordinal) ? Ok("true") : Ok("""{"orderId":"f1","clientOid":"x"}""")));

        await gateway.PlaceAsync(Key, BtcPerp, Order(InstrumentType.Perp, OrderSide.Sell, 0.25m, 61_000m, TimeInForce.PostOnly), CancellationToken.None);
        await gateway.PlaceAsync(Key, BtcPerp, Order(InstrumentType.Perp, OrderSide.Buy, 0.25m, 60_000m, reduceOnly: true), CancellationToken.None);
        Func<Task> notWhole = () => gateway.PlaceAsync(Key, BtcPerp, Order(InstrumentType.Perp, OrderSide.Buy, 0.0005m, 60_000m), CancellationToken.None);

        handler.Sent.Select(s => s.Request.RequestUri!.AbsolutePath).Should().Equal("/api/v2/changeCrossUserLeverage", "/api/v1/orders", "/api/v1/orders");
        handler.Sent[0].Request.RequestUri!.Host.Should().Be("api-futures.kucoin.com");
        handler.Sent[0].Body.Should().Be("""{"symbol":"XBTUSDTM","leverage":"3"}""");
        using JsonDocument first = JsonDocument.Parse(handler.Sent[1].Body);
        first.RootElement.GetProperty("size").GetInt64().Should().Be(250);
        first.RootElement.GetProperty("side").GetString().Should().Be("sell");
        first.RootElement.GetProperty("marginMode").GetString().Should().Be("CROSS");
        first.RootElement.GetProperty("postOnly").GetBoolean().Should().BeTrue();
        first.RootElement.GetProperty("timeInForce").GetString().Should().Be("GTC");
        first.RootElement.GetProperty("reduceOnly").GetBoolean().Should().BeFalse();
        first.RootElement.TryGetProperty("leverage", out _).Should().BeFalse();
        using JsonDocument second = JsonDocument.Parse(handler.Sent[2].Body);
        second.RootElement.GetProperty("reduceOnly").GetBoolean().Should().BeTrue();
        // Half a contract is never rounded into an order.
        await notWhole.Should().ThrowAsync<VenueRefusedException>();
        handler.Sent.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"code":"200004","msg":"Balance insufficient!"}""", true)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"400100","msg":"Order size below the minimum requirement."}""", true)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"code":"429000","msg":"Too Many Requests"}""", false)]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>", false)]
    [InlineData(HttpStatusCode.OK, """{"code":"230005","msg":"The system is busy, please try again later"}""", false)]
    public async Task AnAnswerToAnOrder_IsEitherAClearNo_OrNotAnAnswerAtAll(HttpStatusCode status, string body, bool refused)
    {
        (KucoinOrderGateway gateway, _) = Gateway((_, _) => (status, body));

        Func<Task> place = () => gateway.PlaceAsync(Key, BtcSpot, Order(InstrumentType.Spot, OrderSide.Buy, 0.01m, 61_000m), CancellationToken.None);

        if (refused)
        {
            (await place.Should().ThrowAsync<VenueRefusedException>()).Which.Message.Should().Contain("code ");
        }
        else
        {
            await place.Should().ThrowAsync<VenueUnclearException>();
        }
    }

    [Fact]
    public async Task AConnectionThatFails_OrAnAnswerThatDoesNotComeInTime_IsUnclear()
    {
        KucoinOrderGateway failing = new(new HttpClient(new Stub((_, _) => throw new HttpRequestException("reset"))), new SetClock());
        KucoinOrderGateway slow = new(new HttpClient(new Stub((_, _) => throw new TaskCanceledException("timeout"))), new SetClock());

        await FluentActions.Invoking(() => failing.PlaceAsync(Key, BtcSpot, Order(InstrumentType.Spot, OrderSide.Buy, 0.01m, 61_000m), CancellationToken.None)).Should().ThrowAsync<VenueUnclearException>();
        await FluentActions.Invoking(() => slow.PlaceAsync(Key, BtcSpot, Order(InstrumentType.Spot, OrderSide.Buy, 0.01m, 61_000m), CancellationToken.None)).Should().ThrowAsync<VenueUnclearException>();
    }

    [Fact]
    public async Task SpotOrder_IsReadBack_WithWhatTradedForHowMuch_AndTheFeeWhenItIsInDollars()
    {
        string answer = Ok("""{"id":"67abc","symbol":"BTC-USDT","type":"limit","side":"buy","price":"61000","size":"0.02","dealSize":"0.015","dealFunds":"914.25","fee":"0.91425","feeCurrency":"USDT","cancelExist":false,"inOrderBook":true,"cancelledSize":"0","remainSize":"0.005","active":true}""");
        (KucoinOrderGateway gateway, Stub handler) = Gateway((_, _) => (HttpStatusCode.OK, answer));
        (KucoinOrderGateway inCoin, _) = Gateway((_, _) => (HttpStatusCode.OK, answer.Replace("\"feeCurrency\":\"USDT\"", "\"feeCurrency\":\"KCS\"", StringComparison.Ordinal).Replace("\"active\":true", "\"active\":false", StringComparison.Ordinal)));

        VenueOrderReport open = await gateway.FindAsync(Key, BtcSpot, InstrumentType.Spot, "kft42-a1b2c3d4e5f6", CancellationToken.None);
        VenueOrderReport done = await inCoin.FindAsync(Key, BtcSpot, InstrumentType.Spot, "kft42-a1b2c3d4e5f6", CancellationToken.None);

        handler.Sent.Single().Request.RequestUri!.PathAndQuery.Should().Be("/api/v1/hf/orders/client-order/kft42-a1b2c3d4e5f6?symbol=BTC-USDT");
        open.Should().Be(new VenueOrderReport(VenueOrderState.Open, 0.015m, 914.25m, 0.91425m, "67abc"));
        done.State.Should().Be(VenueOrderState.Done);
        // A fee taken in another coin is not passed off as dollars.
        done.FeeUsd.Should().BeNull();
    }

    [Fact]
    public async Task PerpOrder_IsReadBackInUnitsOfTheAsset_WithItsFeesFromItsFillsOnceItIsDone()
    {
        (KucoinOrderGateway gateway, Stub handler) = Gateway((request, _) =>
            (HttpStatusCode.OK, request.RequestUri!.AbsolutePath == "/api/v1/fills"
                ? Ok("""{"currentPage":1,"pageSize":1000,"totalNum":2,"totalPage":1,"items":[{"orderId":"f1","price":"61000","size":100,"fee":"3.66","feeCurrency":"USDT"},{"orderId":"f1","price":"61010","size":150,"fee":"5.49075","feeCurrency":"USDT"}]}""")
                : Ok("""{"id":"f1","symbol":"XBTUSDTM","type":"limit","side":"buy","price":"61010","size":300,"dealSize":250,"dealValue":"15251.5","filledSize":250,"filledValue":"15251.5","isActive":false,"cancelExist":true,"status":"done","reduceOnly":false}""")));

        VenueOrderReport report = await gateway.FindAsync(Key, BtcPerp, InstrumentType.Perp, "kft42-a1b2c3d4e5f6", CancellationToken.None);

        handler.Sent[0].Request.RequestUri!.PathAndQuery.Should().Be("/api/v1/orders/byClientOid?clientOid=kft42-a1b2c3d4e5f6");
        report.Should().Be(new VenueOrderReport(VenueOrderState.Done, 0.25m, 15_251.5m, 9.15075m, "f1"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"code":"200000","data":null}""")]
    [InlineData(HttpStatusCode.NotFound, """{"code":"400100","msg":"order not exist."}""")]
    [InlineData(HttpStatusCode.OK, """{"code":"100001","msg":"The order does not exist"}""")]
    public async Task OrderKucoinDoesNotKnow_IsReportedAsNotFound_NotAsAnError(HttpStatusCode status, string body)
    {
        (KucoinOrderGateway gateway, _) = Gateway((_, _) => (status, body));

        (await gateway.FindAsync(Key, BtcSpot, InstrumentType.Spot, "kft1-000000000000", CancellationToken.None)).State.Should().Be(VenueOrderState.NotFound);
        (await gateway.FindAsync(Key, BtcPerp, InstrumentType.Perp, "kft1-000000000000", CancellationToken.None)).State.Should().Be(VenueOrderState.NotFound);
    }

    [Theory]
    [InlineData("""{"code":"400006","msg":"Invalid request ip, the current clientIp is:1.2.3.4"}""")]
    [InlineData("""{"code":"400003","msg":"KC-API-KEY not exists"}""")]
    [InlineData("""{"code":"400007","msg":"Access denied: the order does not exist for this key"}""")]
    [InlineData("""{"code":"900001","msg":"symbol not exists"}""")]
    public async Task AnyOtherRefusalWhenAskingAboutAnOrder_IsNotTakenToMeanTheOrderIsNotThere(string body)
    {
        (KucoinOrderGateway gateway, _) = Gateway((_, _) => (HttpStatusCode.Forbidden, body));

        await FluentActions.Invoking(() => gateway.FindAsync(Key, BtcSpot, InstrumentType.Spot, "kft1-000000000000", CancellationToken.None)).Should().ThrowAsync<VenueRefusedException>();
    }

    [Fact]
    public async Task Rules_ComeFromKucoin_AndAnAssetItDoesNotListHasNone()
    {
        (KucoinOrderGateway gateway, Stub handler) = Gateway((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/symbols/BTC-USDT" => (HttpStatusCode.OK, Ok("""{"symbol":"BTC-USDT","baseCurrency":"BTC","quoteCurrency":"USDT","baseMinSize":"0.00001","baseIncrement":"0.00000001","priceIncrement":"0.1","minFunds":"0.1","enableTrading":true}""")),
            "/api/v1/contracts/active" => (HttpStatusCode.OK, Contracts),
            _ => (HttpStatusCode.BadRequest, """{"code":"900001","msg":"symbol not exists"}"""),
        });

        VenueInstrument spot = (await gateway.InstrumentAsync("btc", InstrumentType.Spot, CancellationToken.None))!;
        VenueInstrument perp = (await gateway.InstrumentAsync("BTC", InstrumentType.Perp, CancellationToken.None))!;
        VenueInstrument doge = (await gateway.InstrumentAsync("DOGE", InstrumentType.Perp, CancellationToken.None))!;
        VenueInstrument? none = await gateway.InstrumentAsync("NOSUCH", InstrumentType.Spot, CancellationToken.None);
        await gateway.InstrumentAsync("BTC", InstrumentType.Spot, CancellationToken.None);

        spot.Should().Be(new VenueInstrument("BTC-USDT", 0.00000001m, 0.1m, 0.00001m, 0.1m));
        perp.Should().Be(new VenueInstrument("XBTUSDTM", 0.001m, 0.1m, 0.001m, 0m));
        doge.QuantityStep.Should().Be(100m);
        none.Should().BeNull();
        (await gateway.InstrumentAsync("NOSUCH", InstrumentType.Perp, CancellationToken.None)).Should().BeNull();
        // Rules are asked for once and kept for a while, not before every order.
        handler.Sent.Count(s => s.Request.RequestUri!.AbsolutePath == "/api/v2/symbols/BTC-USDT").Should().Be(1);
        handler.Sent.Count(s => s.Request.RequestUri!.AbsolutePath == "/api/v1/contracts/active").Should().Be(1);
        spot.FloorQuantity(0.123456789123m).Should().Be(0.12345678m);
        perp.FloorQuantity(0.2599m).Should().Be(0.259m);
        spot.RoundPrice(61_000.17m, OrderSide.Buy).Should().Be(61_000.1m);
        spot.RoundPrice(61_000.11m, OrderSide.Sell).Should().Be(61_000.2m);
    }

    [Fact]
    public async Task Account_IsCashCoinsAndPerpetualPositions_AndStandsWithoutAFuturesAccount()
    {
        Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond = (request, _) => request.RequestUri!.PathAndQuery switch
        {
            "/api/v1/hf/accounts/opened" => (HttpStatusCode.OK, Ok("false")),
            "/api/v1/accounts?type=trade" => (HttpStatusCode.OK, Ok("""[{"currency":"USDT","type":"trade","balance":"1500.5","available":"1500.5","holds":"0"},{"currency":"BTC","type":"trade","balance":"0.5","available":"0.5","holds":"0"},{"currency":"KCS","type":"trade","balance":"0","available":"0","holds":"0"}]""")),
            "/api/v1/prices?base=USD" => (HttpStatusCode.OK, Ok("""{"BTC":"61000.1","USDT":"1.0001"}""")),
            "/api/v1/account-overview?currency=USDT" => (HttpStatusCode.OK, Ok("""{"accountEquity":2400.25,"unrealisedPNL":12.5,"marginBalance":2387.75,"currency":"USDT"}""")),
            "/api/v1/contracts/active" => (HttpStatusCode.OK, Contracts),
            "/api/v1/positions" => (HttpStatusCode.OK, Ok("""[{"symbol":"XBTUSDTM","currentQty":-250,"avgEntryPrice":61500.0,"markPrice":61000.0,"liquidationPrice":75000.5,"isOpen":true},{"symbol":"DOGEUSDTM","currentQty":0,"avgEntryPrice":0,"markPrice":0.05,"liquidationPrice":0,"isOpen":false}]""")),
            _ => (HttpStatusCode.NotFound, """{"code":"404","msg":"no"}"""),
        };
        (KucoinOrderGateway gateway, _) = Gateway(respond);
        (KucoinOrderGateway spotOnly, _) = Gateway((request, body) => request.RequestUri!.Host.StartsWith("api-futures", StringComparison.Ordinal) && request.Headers.Contains("KC-API-KEY") ? (HttpStatusCode.Forbidden, """{"code":"400007","msg":"Access denied, require more permission."}""") : respond(request, body));

        VenueAccountReport account = await gateway.AccountAsync(Key, CancellationToken.None);
        VenueAccountReport withoutFutures = await spotOnly.AccountAsync(Key, CancellationToken.None);

        account.CashUsd.Should().Be(1500.5m);
        account.DerivativesEquityUsd.Should().Be(2400.25m);
        account.Holdings.Should().BeEquivalentTo([new VenueHolding("BTC", InstrumentType.Spot, 0.5m, null, null, 61_000.1m), new VenueHolding("BTC", InstrumentType.Perp, -0.25m, 61_500m, 75_000.5m, 61_000m)]);
        withoutFutures.DerivativesEquityUsd.Should().BeNull();
        withoutFutures.Holdings.Should().ContainSingle().Which.Type.Should().Be(InstrumentType.Spot);
    }

    [Fact]
    public async Task KeyOfTheOlderFormat_IsTriedAgainAsSuch_AndRemembered()
    {
        (KucoinOrderGateway gateway, Stub handler) = Gateway((request, _) =>
            request.Headers.GetValues("KC-API-KEY-VERSION").Single() == "3" ? (HttpStatusCode.BadRequest, """{"code":"400005","msg":"Invalid KC-API-SIGN"}""") : (HttpStatusCode.OK, Ok("""{"clientOid":"x"}""")));

        await gateway.CancelAsync(Key, BtcSpot, InstrumentType.Spot, "kft42-a1b2c3d4e5f6", CancellationToken.None);
        await gateway.CancelAsync(Key, BtcSpot, InstrumentType.Spot, "kft42-a1b2c3d4e5f6", CancellationToken.None);

        handler.Sent.Select(s => s.Request.Headers.GetValues("KC-API-KEY-VERSION").Single()).Should().Equal("3", "2", "2");
        handler.Sent[0].Request.Method.Should().Be(HttpMethod.Delete);
        handler.Sent[0].Request.RequestUri!.PathAndQuery.Should().Be("/api/v1/hf/orders/client-order/kft42-a1b2c3d4e5f6?symbol=BTC-USDT");
    }
}

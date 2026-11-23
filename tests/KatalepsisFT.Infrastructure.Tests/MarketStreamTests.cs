using FluentAssertions;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Execution;

namespace KatalepsisFT.Infrastructure.Tests;

/// <summary>Each venue's market data messages, in the shapes the venues send them, read into books.</summary>
public class MarketStreamTests
{
    [Fact]
    public void Binance_ReadsTheWholeTopOfABook_FromACombinedStreamMessage()
    {
        const string message = """{"stream":"btcusdt@depth20@100ms","data":{"e":"depthUpdate","E":1,"T":1,"s":"BTCUSDT","U":1,"u":2,"pu":0,"b":[["62839.50","2.363"],["62839.40","0.010"]],"a":[["62839.60","3.393"],["62839.70","0.000"],["62839.80","1.5"]]}}""";

        StreamedBook book = new BinanceBookStream().Read(message)!;

        book.Asset.Should().Be("BTC");
        book.Bids.Should().Equal(new BookLevel(62839.5m, 2.363m), new BookLevel(62839.4m, 0.01m));
        // A price with nothing offered is not a price.
        book.Asks.Should().Equal(new BookLevel(62839.6m, 3.393m), new BookLevel(62839.8m, 1.5m));
    }

    [Fact]
    public void Bybit_KeepsTheBook_AndAppliesEachChangeToIt()
    {
        BybitBookStream stream = new();

        StreamedBook? early = stream.Read("""{"topic":"orderbook.50.ETHUSDT","type":"delta","data":{"s":"ETHUSDT","b":[["3000.1","1"]],"a":[]}}""");
        StreamedBook whole = stream.Read("""{"topic":"orderbook.50.ETHUSDT","type":"snapshot","ts":1,"data":{"s":"ETHUSDT","b":[["3000.0","5"],["2999.5","7"]],"a":[["3000.5","4"],["3001.0","6"]],"u":1,"seq":1}}""")!;
        StreamedBook changed = stream.Read("""{"topic":"orderbook.50.ETHUSDT","type":"delta","ts":2,"data":{"s":"ETHUSDT","b":[["3000.0","0"],["3000.2","1.5"]],"a":[["3000.5","2"]],"u":2,"seq":2}}""")!;
        StreamedBook? other = stream.Read("""{"success":true,"ret_msg":"","op":"subscribe"}""");
        stream.Reset();
        StreamedBook? afterReconnect = stream.Read("""{"topic":"orderbook.50.ETHUSDT","type":"delta","data":{"s":"ETHUSDT","b":[["3000.1","1"]],"a":[]}}""");

        // A change cannot be applied to a book never received in full: before the first one, and after a reconnection.
        early.Should().BeNull();
        afterReconnect.Should().BeNull();
        other.Should().BeNull();
        whole.Bids.Should().Equal(new BookLevel(3000m, 5m), new BookLevel(2999.5m, 7m));
        changed.Asset.Should().Be("ETH");
        changed.Bids.Should().Equal(new BookLevel(3000.2m, 1.5m), new BookLevel(2999.5m, 7m));
        changed.Asks.Should().Equal(new BookLevel(3000.5m, 2m), new BookLevel(3001m, 6m));
    }

    [Fact]
    public void Okx_TurnsContractsIntoUnitsOfTheAsset()
    {
        OkxBookStream stream = new();
        stream.UseContractSizes(OkxBookStream.ReadContractSizes("""{"code":"0","data":[{"instId":"BTC-USDT-SWAP","ctVal":"0.01"},{"instId":"BTC-USD-SWAP","ctVal":"100"},{"instId":"SOL-USDT-SWAP","ctVal":"1"}]}"""));

        StreamedBook book = stream.Read("""{"arg":{"channel":"books5","instId":"BTC-USDT-SWAP"},"data":[{"asks":[["62840.1","250","0","4"]],"bids":[["62840","1200","0","9"],["62839.9","3","0","1"]],"instId":"BTC-USDT-SWAP","ts":"1","seqId":1}]}""")!;

        book.Asset.Should().Be("BTC");
        book.Bids.Should().Equal(new BookLevel(62840m, 12m), new BookLevel(62839.9m, 0.03m));
        book.Asks.Should().Equal(new BookLevel(62840.1m, 2.5m));
        stream.Read("pong").Should().BeNull();
        stream.Read("""{"event":"subscribe","arg":{"channel":"books5","instId":"BTC-USDT-SWAP"},"connId":"x"}""").Should().BeNull();
        // Only swaps whose contract size is known are asked for.
        stream.Subscriptions(["BTC", "NOSUCH", "SOL"]).Single().Should().Contain("BTC-USDT-SWAP").And.Contain("SOL-USDT-SWAP").And.NotContain("NOSUCH");
    }

    [Fact]
    public void Hyperliquid_ReadsBothSidesOfABook()
    {
        const string message = """{"channel":"l2Book","data":{"coin":"SOL","time":1,"levels":[[{"px":"150.21","sz":"310.5","n":4},{"px":"150.2","sz":"12","n":1}],[{"px":"150.22","sz":"88.01","n":2}]]}}""";
        HyperliquidBookStream stream = new();

        StreamedBook book = stream.Read(message)!;

        book.Asset.Should().Be("SOL");
        book.Bids.Should().Equal(new BookLevel(150.21m, 310.5m), new BookLevel(150.2m, 12m));
        book.Asks.Should().Equal(new BookLevel(150.22m, 88.01m));
        stream.Read("""{"channel":"pong"}""").Should().BeNull();
        stream.Read("""{"channel":"subscriptionResponse","data":{"method":"subscribe","subscription":{"type":"l2Book","coin":"SOL"}}}""").Should().BeNull();
    }

    [Fact]
    public void KucoinSpot_ReadsABook_AndAsksForEveryAssetInOneSubscription()
    {
        KucoinSpotBookStream stream = new();

        StreamedBook book = stream.Read("""{"topic":"/spotMarket/level2Depth50:ETH-USDT","type":"message","subject":"level2","data":{"asks":[["1761.29","2.2615666"],["1761.31","2.1876071"]],"bids":[["1761.28","0.5"]],"timestamp":1733124805073}}""")!;

        book.Asset.Should().Be("ETH");
        stream.Type.Should().Be(InstrumentType.Spot);
        book.Asks.Should().Equal(new BookLevel(1761.29m, 2.2615666m), new BookLevel(1761.31m, 2.1876071m));
        book.Bids.Should().Equal(new BookLevel(1761.28m, 0.5m));
        stream.Read("""{"id":"t1","type":"welcome"}""").Should().BeNull();
        stream.Read("""{"id":"p1","type":"pong","timestamp":1795454772738026}""").Should().BeNull();
        stream.Subscriptions(["BTC", "eth"]).Single().Should().Contain("/spotMarket/level2Depth50:BTC-USDT,ETH-USDT");
        KucoinBookStream.ReadAddress("""{"code":"200000","data":{"token":"abc+/=","instanceServers":[{"endpoint":"wss://ws-api-spot.kucoin.com/","encrypt":true,"protocol":"websocket","pingInterval":18000,"pingTimeout":10000}]}}""")
            .AbsoluteUri.Should().Be("wss://ws-api-spot.kucoin.com/?token=abc%2B%2F%3D");
    }

    [Fact]
    public void KucoinPerp_CallsBitcoinByItsUsualName_AndTurnsContractsIntoUnitsOfTheAsset()
    {
        KucoinPerpBookStream stream = new();
        stream.UseContracts(KucoinPerpBookStream.ReadContracts("""{"code":"200000","data":[{"symbol":"XBTUSDTM","baseCurrency":"XBT","quoteCurrency":"USDT","multiplier":0.001,"status":"Open","expireDate":null},{"symbol":"DOGEUSDTM","baseCurrency":"DOGE","quoteCurrency":"USDT","multiplier":100.0,"status":"Open","expireDate":null},{"symbol":"XBTUSDM","baseCurrency":"XBT","quoteCurrency":"USD","multiplier":-1,"status":"Open","expireDate":null},{"symbol":"ETHUSDTM","baseCurrency":"ETH","quoteCurrency":"USDT","multiplier":0.01,"status":"Paused","expireDate":null}]}"""));

        StreamedBook bitcoin = stream.Read("""{"topic":"/contractMarket/level2Depth50:XBTUSDTM","type":"message","subject":"level2","sn":1,"data":{"sequence":1,"asks":[["61850.3",125],["61851.4",91]],"bids":[["61850.2",2217]],"ts":1,"timestamp":1}}""")!;
        StreamedBook doge = stream.Read("""{"topic":"/contractMarket/level2Depth50:DOGEUSDTM","type":"message","subject":"level2","data":{"asks":[["0.05765",449]],"bids":[["0.05763",886]]}}""")!;

        bitcoin.Asset.Should().Be("BTC");
        bitcoin.Asks.Should().Equal(new BookLevel(61850.3m, 0.125m), new BookLevel(61851.4m, 0.091m));
        bitcoin.Bids.Should().Equal(new BookLevel(61850.2m, 2.217m));
        doge.Bids.Should().Equal(new BookLevel(0.05763m, 88_600m));
        // Only open USDT-margined perpetuals are followed, and an asset without one is simply not asked for.
        stream.Subscriptions(["BTC", "ETH", "DOGE"]).Single().Should().Contain(":XBTUSDTM,DOGEUSDTM").And.NotContain("ETH");
        stream.Read("""{"topic":"/contractMarket/level2Depth50:ETHUSDTM","type":"message","data":{"asks":[],"bids":[]}}""").Should().BeNull();
    }

    [Fact]
    public void BinanceSpot_TakesTheAssetFromTheStreamsName()
    {
        BinanceSpotBookStream stream = new();

        StreamedBook book = stream.Read("""{"stream":"ethusdt@depth20@100ms","data":{"lastUpdateId":160,"bids":[["1761.28","0.5"],["1761.20","0.00000000"]],"asks":[["1761.29","2.26"]]}}""")!;

        book.Asset.Should().Be("ETH");
        stream.Type.Should().Be(InstrumentType.Spot);
        book.Bids.Should().Equal(new BookLevel(1761.28m, 0.5m));
        book.Asks.Should().Equal(new BookLevel(1761.29m, 2.26m));
        stream.Address(["BTC", "eth"]).AbsoluteUri.Should().Be("wss://stream.binance.com:9443/stream?streams=btcusdt@depth20@100ms/ethusdt@depth20@100ms");
        stream.Read("""{"result":null,"id":1}""").Should().BeNull();
    }

    [Fact]
    public void BybitSpot_IsTheSameStreamOnTheSpotAddress_WithItsOwnBooks()
    {
        BybitSpotBookStream spot = new();
        BybitBookStream perp = new();

        StreamedBook book = spot.Read("""{"topic":"orderbook.50.BTCUSDT","ts":1,"type":"snapshot","data":{"s":"BTCUSDT","b":[["61670.1","0.606931"]],"a":[["61670.5","0.1"]],"u":1,"seq":1}}""")!;

        spot.Type.Should().Be(InstrumentType.Spot);
        spot.Address(["BTC"]).AbsoluteUri.Should().Be("wss://stream.bybit.com/v5/public/spot");
        perp.Address(["BTC"]).AbsoluteUri.Should().Be("wss://stream.bybit.com/v5/public/linear");
        book.Bids.Should().Equal(new BookLevel(61670.1m, 0.606931m));
        // The perpetual stream has not seen that book: a change for it there is still a change to nothing.
        perp.Read("""{"topic":"orderbook.50.BTCUSDT","type":"delta","data":{"s":"BTCUSDT","b":[["1","1"]],"a":[]}}""").Should().BeNull();
    }

    [Fact]
    public void OkxSpot_ReadsSizesAsTheyAre_AndIgnoresSwaps()
    {
        OkxSpotBookStream stream = new();

        StreamedBook book = stream.Read("""{"arg":{"channel":"books5","instId":"SOL-USDT"},"data":[{"asks":[["65.14","250.5","0","4"]],"bids":[["65.13","1200","0","9"]],"instId":"SOL-USDT","ts":"1"}]}""")!;

        book.Asset.Should().Be("SOL");
        book.Bids.Should().Equal(new BookLevel(65.13m, 1200m));
        book.Asks.Should().Equal(new BookLevel(65.14m, 250.5m));
        stream.Read("""{"arg":{"channel":"books5","instId":"SOL-USDT-SWAP"},"data":[{"asks":[["65.14","250","0","4"]],"bids":[]}]}""").Should().BeNull();
        stream.Read("pong").Should().BeNull();
        stream.Subscriptions(["sol"]).Single().Should().Contain("\"instId\":\"SOL-USDT\"");
    }

    [Fact]
    public void Bitget_KeepsSpotAndPerpetualsApart_ThoughBothComeOverTheSameAddress()
    {
        BitgetBookStream spot = new(InstrumentType.Spot);
        BitgetBookStream perp = new(InstrumentType.Perp);
        const string spotMessage = """{"action":"snapshot","arg":{"instType":"SPOT","channel":"books15","instId":"BTCUSDT"},"data":[{"asks":[["61670","0.049987"],["61670.75","0.0001"]],"bids":[["61669.99","0.5"]],"checksum":0,"ts":"1"}],"ts":1}""";
        const string perpMessage = """{"action":"snapshot","arg":{"instType":"USDT-FUTURES","channel":"books15","instId":"DOGEUSDT"},"data":[{"asks":[["0.05717","108418"]],"bids":[["0.05716","666269"]]}]}""";

        spot.Read(spotMessage)!.Should().BeEquivalentTo(new StreamedBook("BTC", [new BookLevel(61669.99m, 0.5m)], [new BookLevel(61670m, 0.049987m), new BookLevel(61670.75m, 0.0001m)]));
        perp.Read(perpMessage)!.Should().BeEquivalentTo(new StreamedBook("DOGE", [new BookLevel(0.05716m, 666_269m)], [new BookLevel(0.05717m, 108_418m)]));
        spot.Read(perpMessage).Should().BeNull();
        perp.Read(spotMessage).Should().BeNull();
        spot.Read("pong").Should().BeNull();
        spot.Read("""{"event":"subscribe","arg":{"instType":"SPOT","channel":"books15","instId":"BTCUSDT"}}""").Should().BeNull();
        perp.Subscriptions(["doge"]).Single().Should().Contain("USDT-FUTURES").And.Contain("DOGEUSDT");
    }

    [Fact]
    public void Gate_ReadsSpotAsItIs_AndTurnsPerpetualContractsIntoUnitsOfTheAsset()
    {
        GateSpotBookStream spot = new();
        GatePerpBookStream perp = new();
        perp.UseContractSizes(perp.ReadContractSizes("""[{"name":"BTC_USDT","quanto_multiplier":"0.0001","in_delisting":false,"status":"trading"},{"name":"DOGE_USDT","quanto_multiplier":"10","in_delisting":false,"status":"trading"},{"name":"OLD_USDT","quanto_multiplier":"1","in_delisting":true,"status":"trading"}]"""));

        StreamedBook spotBook = spot.Read("""{"time":1,"time_ms":1,"channel":"spot.order_book","event":"update","result":{"t":1,"lastUpdateId":4,"s":"BTC_USDT","l":"20","bids":[["61666.5","0.579459"]],"asks":[["61666.6","0.1"]]}}""")!;
        StreamedBook perpBook = perp.Read("""{"time":1,"channel":"futures.order_book","event":"all","result":{"t":1,"id":4,"contract":"DOGE_USDT","asks":[{"p":"0.05717","s":41684}],"bids":[{"p":"0.05716","s":2306}]}}""")!;

        spotBook.Should().BeEquivalentTo(new StreamedBook("BTC", [new BookLevel(61666.5m, 0.579459m)], [new BookLevel(61666.6m, 0.1m)]));
        perpBook.Should().BeEquivalentTo(new StreamedBook("DOGE", [new BookLevel(0.05716m, 23_060m)], [new BookLevel(0.05717m, 416_840m)]));
        spot.Read("""{"time":1,"channel":"spot.order_book","event":"subscribe","payload":["BTC_USDT","20","100ms"],"result":{"status":"success"}}""").Should().BeNull();
        spot.Read("""{"time":1,"channel":"spot.pong","event":"","result":null}""").Should().BeNull();
        // A contract that is being delisted is not followed, nor one the venue does not have.
        perp.Subscriptions(["BTC", "OLD", "NOSUCH", "DOGE"]).Should().HaveCount(2);
        spot.Subscriptions(["BTC", "ETH"]).Should().HaveCount(2).And.OnlyContain(m => m.Contains("\"event\":\"subscribe\""));
    }

    [Fact]
    public void Mexc_TurnsPerpetualContractsIntoUnitsOfTheAsset()
    {
        MexcPerpBookStream stream = new();
        stream.UseContractSizes(stream.ReadContractSizes("""{"success":true,"code":0,"data":[{"symbol":"BTC_USDT","contractSize":0.0001,"state":0,"quoteCoin":"USDT","settleCoin":"USDT"},{"symbol":"DOGE_USDT","contractSize":100,"state":0,"quoteCoin":"USDT","settleCoin":"USDT"},{"symbol":"HALT_USDT","contractSize":1,"state":2,"quoteCoin":"USDT","settleCoin":"USDT"},{"symbol":"BTC_USD","contractSize":100,"state":0,"quoteCoin":"USD","settleCoin":"BTC"}]}"""));

        StreamedBook book = stream.Read("""{"symbol":"DOGE_USDT","data":{"cts":1,"asks":[[0.057170,5860,1],[0.057180,12478,1]],"bids":[[0.057160,300,2]],"version":7},"channel":"push.depth.full","ts":1}""")!;

        book.Should().BeEquivalentTo(new StreamedBook("DOGE", [new BookLevel(0.05716m, 30_000m)], [new BookLevel(0.05717m, 586_000m), new BookLevel(0.05718m, 1_247_800m)]));
        stream.Read("""{"channel":"rs.sub.depth.full","data":"success","ts":1}""").Should().BeNull();
        stream.Read("""{"channel":"pong","data":1,"ts":1}""").Should().BeNull();
        stream.Subscriptions(["BTC", "HALT", "DOGE"]).Should().HaveCount(2);
    }

    [Fact]
    public void Coinbase_KeepsTheBookFromItsFirstCopy_AndAppliesEachBatchOfChanges()
    {
        CoinbaseSpotBookStream stream = new();

        StreamedBook? early = stream.Read("""{"type":"l2update","product_id":"BTC-USD","changes":[["buy","60951.01","1"]]}""");
        StreamedBook whole = stream.Read("""{"type":"snapshot","product_id":"BTC-USD","asks":[["60977.28","0.5"],["60978.19","0.2"]],"bids":[["60977.27","0.13"],["60975.99","0.4"]]}""")!;
        StreamedBook changed = stream.Read("""{"type":"l2update","product_id":"BTC-USD","changes":[["buy","60977.27","0.00000000"],["buy","60976.50","0.7"],["sell","60977.28","0.9"]],"time":"2026-11-23T19:40:35Z"}""")!;

        early.Should().BeNull();
        stream.Quote.Should().Be("USD");
        whole.Bids.Should().Equal(new BookLevel(60977.27m, 0.13m), new BookLevel(60975.99m, 0.4m));
        changed.Asset.Should().Be("BTC");
        changed.Bids.Should().Equal(new BookLevel(60976.5m, 0.7m), new BookLevel(60975.99m, 0.4m));
        changed.Asks.Should().Equal(new BookLevel(60977.28m, 0.9m), new BookLevel(60978.19m, 0.2m));
        stream.Read("""{"type":"heartbeat","sequence":1,"product_id":"BTC-USD","time":"2026-11-23T19:40:35Z"}""").Should().BeNull();
        stream.Reset();
        stream.Read("""{"type":"l2update","product_id":"BTC-USD","changes":[["buy","60951.01","1"]]}""").Should().BeNull();
    }

    [Fact]
    public void Kraken_KeepsOnlyTheBestPrices_AsABetterOneComesIn()
    {
        KrakenSpotBookStream stream = new();
        string bids = string.Join(",", Enumerable.Range(0, 25).Select(i => $$"""{"price":{{100 - i}}.0,"qty":1.5}"""));

        StreamedBook whole = stream.Read($$"""{"channel":"book","type":"snapshot","data":[{"symbol":"DOGE/USD","bids":[{{bids}}],"asks":[{"price":101.0,"qty":2.0}],"checksum":1}]}""")!;
        StreamedBook better = stream.Read("""{"channel":"book","type":"update","data":[{"symbol":"DOGE/USD","bids":[{"price":100.5,"qty":3.0},{"price":99.0,"qty":0}],"asks":[],"checksum":2,"timestamp":"2026-11-23T19:40:35.517308Z"}]}""")!;

        whole.Asset.Should().Be("DOGE");
        whole.Bids.Should().HaveCount(20);
        better.Bids[0].Should().Be(new BookLevel(100.5m, 3m));
        better.Bids.Select(b => b.Price).Should().NotContain(99m);
        better.Asks.Should().Equal(new BookLevel(101m, 2m));
        stream.Read("""{"channel":"heartbeat"}""").Should().BeNull();
        stream.Read("""{"channel":"status","type":"update","data":[{"system":"online"}]}""").Should().BeNull();
        stream.Subscriptions(["btc", "DOGE"]).Single().Should().Contain("BTC/USD").And.Contain("DOGE/USD").And.Contain("\"depth\":25");
    }

    [Fact]
    public void Deribit_FollowsItsUsdcPerpetuals_SizedInUnitsOfTheAsset()
    {
        DeribitPerpBookStream stream = new();
        stream.UseListed(DeribitPerpBookStream.ReadListed("""{"jsonrpc":"2.0","result":[{"instrument_name":"BTC_USDC-PERPETUAL","instrument_type":"linear","is_active":true},{"instrument_name":"SOL_USDC-PERPETUAL","instrument_type":"linear","is_active":true},{"instrument_name":"BTC_USDC-27DEC26","instrument_type":"linear","is_active":true}]}"""));

        StreamedBook book = stream.Read("""{"jsonrpc":"2.0","method":"subscription","params":{"channel":"book.BTC_USDC-PERPETUAL.none.20.100ms","data":{"timestamp":1,"change_id":2,"instrument_name":"BTC_USDC-PERPETUAL","bids":[[60982.4,2.3759],[60982.0,0.1552]],"asks":[[60982.5,0.5]]}}}""")!;

        book.Should().BeEquivalentTo(new StreamedBook("BTC", [new BookLevel(60982.4m, 2.3759m), new BookLevel(60982m, 0.1552m)], [new BookLevel(60982.5m, 0.5m)]));
        stream.Quote.Should().Be("USDC");
        stream.Read("""{"jsonrpc":"2.0","id":9,"result":{"version":"1.2.26"}}""").Should().BeNull();
        // An asset Deribit has no such perpetual for is not asked for.
        stream.Subscriptions(["BTC", "BNB", "SOL"]).Single().Should().Contain("book.BTC_USDC-PERPETUAL.none.20.100ms").And.Contain("SOL_USDC").And.NotContain("BNB");
    }

    [Fact]
    public void ConsolidatedBook_DropsAVenueWhoseStreamFell_AndOneThatHasGoneQuiet()
    {
        SetClock clock = new() { Now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero) };
        ConsolidatedBook books = new(clock);
        VenueStatusBoard board = new(clock);
        books.Update(new VenueBook(Venue.Binance, "BTC", InstrumentType.Perp, [new(100m, 1m)], [new(101m, 1m)], clock.GetUtcNow()));
        books.Update(new VenueBook(Venue.Bybit, "BTC", InstrumentType.Perp, [new(100.5m, 1m)], [new(101.5m, 1m)], clock.GetUtcNow()));
        board.Heard(Venue.Binance);
        board.Heard(Venue.Bybit);
        foreach (double sample in new[] { 30d, 10d, 500d })
        {
            board.RoundTrip(Venue.Binance, sample);
        }

        books.Clear(Venue.Bybit);
        IReadOnlyList<VenueBook> afterDrop = books.For("btc", InstrumentType.Perp);
        ExecutionStatus live = board.Snapshot(books, [Venue.Binance, Venue.Bybit, Venue.Okx]);
        clock.Now += TimeSpan.FromSeconds(6);
        ExecutionStatus quiet = board.Snapshot(books, [Venue.Binance]);

        afterDrop.Select(b => b.Venue).Should().Equal(Venue.Binance);
        live.Venues.Select(v => (v.Venue, v.Connected, v.Books)).Should().Equal((Venue.Binance, true, 1), (Venue.Bybit, true, 0), (Venue.Okx, false, 0));
        // The middle of the samples: one slow request does not move it.
        live.Venues[0].RoundTripMs.Should().Be(30d);
        books.For("BTC", InstrumentType.Perp).Should().BeEmpty();
        quiet.Venues.Single().Connected.Should().BeFalse();
    }

    private sealed class SetClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

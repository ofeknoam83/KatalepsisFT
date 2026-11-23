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

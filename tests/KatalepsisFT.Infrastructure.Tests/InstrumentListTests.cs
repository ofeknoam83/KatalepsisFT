using FluentAssertions;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Infrastructure.Execution;

namespace KatalepsisFT.Infrastructure.Tests;

/// <summary>The venues' public instrument lists, in the shapes the venues send them, and the list of wanted books built on them.</summary>
public class InstrumentListTests
{
    private static InstrumentListing Of(Venue venue, InstrumentType type, string? quote = null) => InstrumentListings.All.Single(l => l.Venue == venue && l.Type == type && (quote is null ? l.Quote != "USDC" || !InstrumentListings.All.Any(o => o.Venue == venue && o.Type == type && o.Quote == "USDT") : l.Quote == quote));

    [Fact]
    public void EveryMarketWithABookStream_HasAList()
    {
        InstrumentListings.All.Select(l => (l.Venue, l.Type, l.Quote)).Should().OnlyHaveUniqueItems().And.HaveCount(20);
    }

    [Fact]
    public void BinanceSpot_KeepsUsdtPairs_AndSaysWhichAreNotTrading()
    {
        IReadOnlyList<ListedSymbol> listed = Of(Venue.Binance, InstrumentType.Spot).ReadAnswer(
            """{"symbols":[{"symbol":"ETHBTC","status":"TRADING","baseAsset":"ETH","quoteAsset":"BTC"},{"symbol":"PENDLEUSDT","status":"TRADING","baseAsset":"PENDLE","quoteAsset":"USDT"},{"symbol":"LUNAUSDT","status":"BREAK","baseAsset":"LUNA","quoteAsset":"USDT"}]}""");

        listed.Should().Equal(new ListedSymbol("LUNA", "LUNAUSDT", false), new ListedSymbol("PENDLE", "PENDLEUSDT", true));
    }

    [Fact]
    public void BinancePerp_LeavesOutDatedContracts()
    {
        IReadOnlyList<ListedSymbol> listed = Of(Venue.Binance, InstrumentType.Perp).ReadAnswer(
            """{"symbols":[{"symbol":"BTCUSDT","status":"TRADING","contractType":"PERPETUAL","baseAsset":"BTC","quoteAsset":"USDT"},{"symbol":"BTCUSDT_261225","status":"TRADING","contractType":"CURRENT_QUARTER","baseAsset":"BTC","quoteAsset":"USDT"},{"symbol":"1000PEPEUSDT","status":"TRADING","contractType":"PERPETUAL","baseAsset":"1000PEPE","quoteAsset":"USDT"},{"symbol":"BTCUSDC","status":"TRADING","contractType":"PERPETUAL","baseAsset":"BTC","quoteAsset":"USDC"}]}""");

        // A contract counted in thousands keeps its own name: it is not the same quantity as the coin.
        listed.Select(l => l.Asset).Should().Equal("1000PEPE", "BTC");
    }

    [Fact]
    public void Bybit_ReadsSpotAndLinearPerpetuals()
    {
        Of(Venue.Bybit, InstrumentType.Spot).ReadAnswer("""{"retCode":0,"result":{"category":"spot","list":[{"symbol":"ARBUSDT","baseCoin":"ARB","quoteCoin":"USDT","status":"Trading"},{"symbol":"BTCUSDC","baseCoin":"BTC","quoteCoin":"USDC","status":"Trading"}]}}""")
            .Should().Equal(new ListedSymbol("ARB", "ARBUSDT", true));
        Of(Venue.Bybit, InstrumentType.Perp).ReadAnswer("""{"retCode":0,"result":{"category":"linear","list":[{"symbol":"ARBUSDT","baseCoin":"ARB","quoteCoin":"USDT","status":"Trading","contractType":"LinearPerpetual"},{"symbol":"BTCUSDT-26DEC26","baseCoin":"BTC","quoteCoin":"USDT","status":"Trading","contractType":"LinearFutures"},{"symbol":"TIAUSDT","baseCoin":"TIA","quoteCoin":"USDT","status":"Closed","contractType":"LinearPerpetual"}]}}""")
            .Should().Equal(new ListedSymbol("ARB", "ARBUSDT", true), new ListedSymbol("TIA", "TIAUSDT", false));
    }

    [Fact]
    public void Okx_ReadsTheAssetOfASwapFromItsName()
    {
        // A swap carries no base currency of its own; one that has not opened yet is listed but cannot be traded.
        Of(Venue.Okx, InstrumentType.Perp).ReadAnswer("""{"code":"0","data":[{"instId":"JD-USDT-SWAP","baseCcy":"","quoteCcy":"","state":"preopen"},{"instId":"BTC-USDT-SWAP","baseCcy":"","quoteCcy":"","state":"live"},{"instId":"BTC-USD-SWAP","baseCcy":"","quoteCcy":"","state":"live"}]}""")
            .Should().Equal(new ListedSymbol("BTC", "BTC-USDT-SWAP", true), new ListedSymbol("JD", "JD-USDT-SWAP", false));
        Of(Venue.Okx, InstrumentType.Spot).ReadAnswer("""{"code":"0","data":[{"instId":"BTC-PLN","baseCcy":"BTC","quoteCcy":"PLN","state":"live"},{"instId":"SOL-USDT","baseCcy":"SOL","quoteCcy":"USDT","state":"live"}]}""")
            .Should().Equal(new ListedSymbol("SOL", "SOL-USDT", true));
    }

    [Fact]
    public void KucoinPerp_CallsBitcoinByItsUsualName()
    {
        Of(Venue.Kucoin, InstrumentType.Perp).ReadAnswer("""{"code":"200000","data":[{"symbol":"XBTUSDTM","baseCurrency":"XBT","quoteCurrency":"USDT","status":"Open","isInverse":false},{"symbol":"XBTUSDM","baseCurrency":"XBT","quoteCurrency":"USD","status":"Open","isInverse":true},{"symbol":"DOGEUSDTM","baseCurrency":"DOGE","quoteCurrency":"USDT","status":"Paused","isInverse":false}]}""")
            .Should().Equal(new ListedSymbol("BTC", "XBTUSDTM", true), new ListedSymbol("DOGE", "DOGEUSDTM", false));
    }

    [Fact]
    public void ANameTheSystemCannotUse_IsLeftOut()
    {
        // Bitget lists coins whose names have small letters; an order's asset is capital letters and digits only.
        Of(Venue.Bitget, InstrumentType.Spot).ReadAnswer("""{"data":[{"symbol":"RNNNNUSDT","baseCoin":"rNNNN","quoteCoin":"USDT","status":"online"},{"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","status":"online"}]}""")
            .Should().Equal(new ListedSymbol("BTC", "BTCUSDT", true));
        // Hyperliquid's kPEPE is a thousand PEPE: the same thing other venues call 1000PEPE.
        Of(Venue.Hyperliquid, InstrumentType.Perp).ReadAnswer("""{"universe":[{"name":"BTC","szDecimals":5},{"name":"kPEPE","szDecimals":0},{"name":"MATIC","szDecimals":1,"isDelisted":true},{"name":"oddName","szDecimals":1}]}""")
            .Should().Equal(new ListedSymbol("1000PEPE", "kPEPE", true, null, 1m, 1m), new ListedSymbol("BTC", "BTC", true, null, 0.00001m, 0.00001m), new ListedSymbol("MATIC", "MATIC", false, null, 0.1m, 0.1m));
        HyperliquidBookStream.CoinOfAsset("1000PEPE").Should().Be("kPEPE");
        HyperliquidBookStream.CoinOfAsset("BTC").Should().Be("BTC");
        new HyperliquidBookStream().Subscriptions(["1000pepe"]).Single().Should().Contain("\"coin\":\"kPEPE\"");
        new HyperliquidBookStream().Read("""{"channel":"l2Book","data":{"coin":"kPEPE","levels":[[{"px":"0.0035","sz":"100","n":1}],[{"px":"0.0036","sz":"50","n":1}]]}}""")!.Asset.Should().Be("1000PEPE");
    }

    [Fact]
    public void TheOtherVenues_ReadTheirOwnShapes()
    {
        Of(Venue.Gate, InstrumentType.Spot).ReadAnswer("""[{"id":"ALEPH_USDT","base":"ALEPH","quote":"USDT","trade_status":"tradable"},{"id":"ETH_BTC","base":"ETH","quote":"BTC","trade_status":"tradable"}]""").Should().Equal(new ListedSymbol("ALEPH", "ALEPH_USDT", true));
        Of(Venue.Gate, InstrumentType.Perp).ReadAnswer("""[{"name":"0G_USDT","in_delisting":false},{"name":"OLD_USDT","in_delisting":true}]""").Should().Equal(new ListedSymbol("0G", "0G_USDT", true), new ListedSymbol("OLD", "OLD_USDT", false));
        Of(Venue.Mexc, InstrumentType.Spot).ReadAnswer("""{"symbols":[{"symbol":"METALUSDT","status":"1","baseAsset":"METAL","quoteAsset":"USDT","isSpotTradingAllowed":true},{"symbol":"OFFUSDT","status":"1","baseAsset":"OFF","quoteAsset":"USDT","isSpotTradingAllowed":false}]}""").Should().Equal(new ListedSymbol("METAL", "METALUSDT", true), new ListedSymbol("OFF", "OFFUSDT", false));
        Of(Venue.Mexc, InstrumentType.Perp).ReadAnswer("""{"success":true,"data":[{"symbol":"BTC_USDT","baseCoin":"BTC","quoteCoin":"USDT","state":0},{"symbol":"GONE_USDT","baseCoin":"GONE","quoteCoin":"USDT","state":3}]}""").Should().Equal(new ListedSymbol("BTC", "BTC_USDT", true), new ListedSymbol("GONE", "GONE_USDT", false));
        Of(Venue.CoinbaseExchange, InstrumentType.Spot).ReadAnswer("""[{"id":"ARX-USD","base_currency":"ARX","quote_currency":"USD","status":"online","trading_disabled":false},{"id":"BTC-EUR","base_currency":"BTC","quote_currency":"EUR","status":"online","trading_disabled":false},{"id":"OFF-USD","base_currency":"OFF","quote_currency":"USD","status":"online","trading_disabled":true}]""").Should().Equal(new ListedSymbol("ARX", "ARX-USD", true), new ListedSymbol("OFF", "OFF-USD", false));
        Of(Venue.Kraken, InstrumentType.Spot).ReadAnswer("""{"error":[],"result":{"XXBTZUSD":{"wsname":"XBT/USD","status":"online"},"XDGUSD":{"wsname":"XDG/USD","status":"online"},"0GEUR":{"wsname":"0G/EUR","status":"online"},"SOLUSD":{"wsname":"SOL/USD","status":"cancel_only"}}}""").Should().Equal(new ListedSymbol("BTC", "BTC/USD", true), new ListedSymbol("DOGE", "DOGE/USD", true), new ListedSymbol("SOL", "SOL/USD", false));
        Of(Venue.Deribit, InstrumentType.Perp).ReadAnswer("""{"result":[{"instrument_name":"1000BONK_USDC-PERPETUAL","is_active":true},{"instrument_name":"BTC_USDC-27DEC26","is_active":true}]}""").Should().Equal(new ListedSymbol("1000BONK", "1000BONK_USDC-PERPETUAL", true));
        Of(Venue.Kucoin, InstrumentType.Spot).ReadAnswer("""{"data":[{"symbol":"AVA-USDT","baseCurrency":"AVA","quoteCurrency":"USDT","enableTrading":true},{"symbol":"AVA-BTC","baseCurrency":"AVA","quoteCurrency":"BTC","enableTrading":true}]}""").Should().Equal(new ListedSymbol("AVA", "AVA-USDT", true));
        Of(Venue.Bitget, InstrumentType.Perp).ReadAnswer("""{"data":[{"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","symbolStatus":"normal"},{"symbol":"OLDUSDT","baseCoin":"OLD","quoteCoin":"USDT","symbolStatus":"off"}]}""").Should().Equal(new ListedSymbol("BTC", "BTCUSDT", true), new ListedSymbol("OLD", "OLDUSDT", false));
    }

    [Fact]
    public void Steps_AreReadInUnitsOfTheAsset_AlsoWhereTheVenueCountsInContracts()
    {
        Of(Venue.Binance, InstrumentType.Spot).ReadAnswer("""{"symbols":[{"symbol":"BTCUSDT","status":"TRADING","baseAsset":"BTC","quoteAsset":"USDT","filters":[{"filterType":"PRICE_FILTER","tickSize":"0.01000000"},{"filterType":"LOT_SIZE","minQty":"0.00001000","stepSize":"0.00001000"}]}]}""")
            .Single().Should().Be(new ListedSymbol("BTC", "BTCUSDT", true, 0.01m, 0.00001m, 0.00001m));
        // One OKX swap contract is a hundredth of a bitcoin, and the smallest order is a hundredth of a contract.
        Of(Venue.Okx, InstrumentType.Perp).ReadAnswer("""{"data":[{"instId":"BTC-USDT-SWAP","state":"live","tickSz":"0.1","lotSz":"0.01","minSz":"0.01","ctVal":"0.01"}]}""")
            .Single().Should().Be(new ListedSymbol("BTC", "BTC-USDT-SWAP", true, 0.1m, 0.0001m, 0.0001m));
        Of(Venue.Kucoin, InstrumentType.Perp).ReadAnswer("""{"data":[{"symbol":"XBTUSDTM","baseCurrency":"XBT","quoteCurrency":"USDT","status":"Open","isInverse":false,"tickSize":0.1,"lotSize":1,"multiplier":0.001}]}""")
            .Single().Should().Be(new ListedSymbol("BTC", "XBTUSDTM", true, 0.1m, 0.001m, 0.001m));
        // Bitget gives decimal places for spot, and for perpetuals a step counted in the last decimal place.
        Of(Venue.Bitget, InstrumentType.Spot).ReadAnswer("""{"data":[{"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","status":"online","pricePrecision":"2","quantityPrecision":"6","minTradeAmount":"0"}]}""")
            .Single().Should().Be(new ListedSymbol("BTC", "BTCUSDT", true, 0.01m, 0.000001m, null));
        Of(Venue.Bitget, InstrumentType.Perp).ReadAnswer("""{"data":[{"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","symbolStatus":"normal","pricePlace":"1","priceEndStep":"5","sizeMultiplier":"0.0001","minTradeNum":"0.0001"}]}""")
            .Single().Should().Be(new ListedSymbol("BTC", "BTCUSDT", true, 0.5m, 0.0001m, 0.0001m));
        Of(Venue.Gate, InstrumentType.Perp).ReadAnswer("""[{"name":"BTC_USDT","in_delisting":false,"order_price_round":"0.1","quanto_multiplier":"0.0001","order_size_min":1}]""")
            .Single().Should().Be(new ListedSymbol("BTC", "BTC_USDT", true, 0.1m, 0.0001m, 0.0001m));
        Of(Venue.Kraken, InstrumentType.Spot).ReadAnswer("""{"result":{"XXBTZUSD":{"wsname":"XBT/USD","status":"online","tick_size":"0.1","lot_decimals":8,"ordermin":"0.00006"}}}""")
            .Single().Should().Be(new ListedSymbol("BTC", "BTC/USD", true, 0.1m, 0.00000001m, 0.00006m));
        Of(Venue.Mexc, InstrumentType.Perp).ReadAnswer("""{"data":[{"symbol":"BTC_USDT","baseCoin":"BTC","quoteCoin":"USDT","state":0,"priceUnit":0.1,"volUnit":1,"minVol":1,"contractSize":0.0001}]}""")
            .Single().Should().Be(new ListedSymbol("BTC", "BTC_USDT", true, 0.1m, 0.0001m, 0.0001m));
    }

    [Fact]
    public void BinanceAndBybit_AlsoListPerpetualsMarginedInUsdc()
    {
        const string binance = """{"symbols":[{"symbol":"BTCUSDT","status":"TRADING","contractType":"PERPETUAL","baseAsset":"BTC","quoteAsset":"USDT"},{"symbol":"BTCUSDC","status":"TRADING","contractType":"PERPETUAL","baseAsset":"BTC","quoteAsset":"USDC"}]}""";
        Of(Venue.Binance, InstrumentType.Perp, "USDC").ReadAnswer(binance).Should().Equal(new ListedSymbol("BTC", "BTCUSDC", true));
        Of(Venue.Binance, InstrumentType.Perp, "USDT").ReadAnswer(binance).Should().Equal(new ListedSymbol("BTC", "BTCUSDT", true));
        // Bybit names them after the asset with PERP on the end.
        const string bybit = """{"result":{"list":[{"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","status":"Trading","contractType":"LinearPerpetual"},{"symbol":"BTCPERP","baseCoin":"BTC","quoteCoin":"USDC","status":"Trading","contractType":"LinearPerpetual"},{"symbol":"BTC-26DEC26","baseCoin":"BTC","quoteCoin":"USDC","status":"Trading","contractType":"LinearFutures"}]}}""";
        Of(Venue.Bybit, InstrumentType.Perp, "USDC").ReadAnswer(bybit).Should().Equal(new ListedSymbol("BTC", "BTCPERP", true));

        BinanceUsdcPerpBookStream binanceStream = new();
        binanceStream.Subscriptions(["BTC"]).Single().Should().Contain("btcusdc@depth20@100ms");
        binanceStream.Read("""{"stream":"btcusdc@depth20@100ms","data":{"s":"BTCUSDC","b":[["58757.3","1"]],"a":[["58757.4","2"]]}}""")!.Asset.Should().Be("BTC");
        // A USDT book that strays onto the USDC stream is not taken for a USDC one.
        binanceStream.Read("""{"stream":"btcusdt@depth20@100ms","data":{"s":"BTCUSDT","b":[["58833.8","1"]],"a":[["58833.9","2"]]}}""").Should().BeNull();
        BybitUsdcPerpBookStream bybitStream = new();
        bybitStream.Quote.Should().Be("USDC");
        bybitStream.Subscriptions(["eth"]).Single().Should().Be("""{"op":"subscribe","args":["orderbook.50.ETHPERP"]}""");
        bybitStream.Read("""{"topic":"orderbook.50.ETHPERP","type":"snapshot","data":{"s":"ETHPERP","b":[["3000.1","1"]],"a":[["3000.2","1"]]}}""")!.Asset.Should().Be("ETH");
        bybitStream.Read("""{"topic":"orderbook.50.ETHUSDT","type":"snapshot","data":{"s":"ETHUSDT","b":[["3000.1","1"]],"a":[["3000.2","1"]]}}""").Should().BeNull();
    }

    [Fact]
    public void AVenuesSecondMarket_FollowsOnlyWhatItLists()
    {
        BookDemand demand = new(["BTC", "DOGE"]);
        demand.Want(held: [], asked: [("ARB", InstrumentType.Perp)]);

        // Before its list is read, the USDC market follows nothing; the USDT one follows the main assets as always.
        demand.For(Venue.Binance, InstrumentType.Perp, "USDC", second: true).Should().BeEmpty();
        demand.For(Venue.Binance, InstrumentType.Perp).Should().Equal("BTC", "DOGE");

        demand.Listed(Venue.Binance, InstrumentType.Perp, ["BTC", "ARB"], "USDC");
        demand.Listed(Venue.Binance, InstrumentType.Perp, ["BTC", "DOGE"]);
        // In order of importance: what is always followed first.
        demand.For(Venue.Binance, InstrumentType.Perp, "USDC", second: true).Should().Equal("BTC", "ARB");
        demand.For(Venue.Binance, InstrumentType.Perp).Should().Equal("BTC", "DOGE");
    }

    [Fact]
    public void AVenueWithTwoBooksForOneInstrument_GivesTheOneInTheWantedCurrency()
    {
        KatalepsisFT.Application.Execution.ConsolidatedBook books = new(TimeProvider.System);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        books.Update(new(Venue.Binance, "BTC", InstrumentType.Perp, [new(100m, 1m)], [new(101m, 1m)], now));
        books.Update(new(Venue.Binance, "BTC", InstrumentType.Perp, [new(99m, 1m)], [new(100m, 1m)], now, "USDC"));
        books.Update(new(Venue.Deribit, "BTC", InstrumentType.Perp, [new(98m, 1m)], [new(99m, 1m)], now, "USDC"));

        books.Of(Venue.Binance, "BTC", InstrumentType.Perp)!.BestBid.Should().Be(100m);
        books.Of(Venue.Binance, "BTC", InstrumentType.Perp, "USDC")!.BestBid.Should().Be(99m);
        // A venue with only one book gives it whatever it is in.
        books.Of(Venue.Deribit, "BTC", InstrumentType.Perp)!.BestBid.Should().Be(98m);
        books.Instruments().Single().Venues.Should().Equal(Venue.Binance, Venue.Deribit);

        // The USDC stream dropping takes only its own books with it.
        books.Clear(Venue.Binance, InstrumentType.Perp, "USDC");
        books.Of(Venue.Binance, "BTC", InstrumentType.Perp, "USDC").Should().BeNull();
        books.Of(Venue.Binance, "BTC", InstrumentType.Perp)!.BestBid.Should().Be(100m);
    }

    [Fact]
    public void AnAnswerThatIsNotAList_ReadsAsNothing()
    {
        // A venue that answers with an error object must not be taken for a venue that lists nothing; the caller
        // keeps the last list when this comes back empty.
        Of(Venue.Binance, InstrumentType.Spot).ReadAnswer("""{"code":0,"msg":"Service unavailable from a restricted location"}""").Should().BeEmpty();
    }

    [Fact]
    public void WantedBooks_AreTheAlwaysFollowed_WhatIsHeld_AndWhatWasAskedFor()
    {
        BookDemand demand = new(["BTC", "ETH"], most: 2);
        demand.Listed(Venue.Okx, InstrumentType.Perp, ["BTC", "ETH", "ARB", "TIA", "INJ", "SUI"]);

        demand.Want(held: [("SUI", InstrumentType.Perp)], asked: [("ARB", InstrumentType.Perp), ("BTC", InstrumentType.Perp), ("TIA", InstrumentType.Perp), ("INJ", InstrumentType.Perp)]);

        // The limit counts only what is open because somebody asked: BTC is followed anyway, so INJ is the one over.
        // In order of importance, so that a stream which can only follow so many keeps the right ones: what is
        // always followed, then what is held, then what was asked for.
        demand.For(Venue.Okx, InstrumentType.Perp).Should().Equal("BTC", "ETH", "SUI", "ARB", "TIA");
        demand.For(Venue.Okx, InstrumentType.Spot).Should().Equal("BTC", "ETH");

        demand.Want(held: [], asked: []);
        demand.For(Venue.Okx, InstrumentType.Perp).Should().Equal("BTC", "ETH");
    }

    [Fact]
    public void AVenueIsOnlyAskedForWhatItLists()
    {
        BookDemand demand = new(["BTC"]);
        demand.Listed(Venue.Deribit, InstrumentType.Perp, ["BTC", "ETH"]);
        demand.Want(held: [], asked: [("PENDLE", InstrumentType.Perp), ("eth", InstrumentType.Perp)]);

        demand.For(Venue.Deribit, InstrumentType.Perp).Should().Equal("BTC", "ETH");
        // Once a venue's list is known it decides, also for what is always followed: a venue is not asked for a
        // book it does not list, because that can make it refuse the others asked for with it.
        demand.Listed(Venue.Mexc, InstrumentType.Spot, ["ETH"]);
        demand.For(Venue.Mexc, InstrumentType.Spot).Should().BeEmpty();
        // A venue whose list has not been read is asked only for what was always followed.
        demand.For(Venue.Bybit, InstrumentType.Perp).Should().Equal("BTC");
    }

    [Fact]
    public void EveryStream_UndoesARequestWithTheSameMessageUnderTheOppositeName()
    {
        OkxBookStream okx = new();
        okx.UseContractSizes(new Dictionary<string, decimal> { ["BTC-USDT-SWAP"] = 0.01m });
        okx.Unsubscriptions(["BTC"]).Single().Should().Be("""{"op":"unsubscribe","args":[{"channel":"books5","instId":"BTC-USDT-SWAP"}]}""");
        new BybitBookStream().Unsubscriptions(["ARB"]).Single().Should().Be("""{"op":"unsubscribe","args":["orderbook.50.ARBUSDT"]}""");
        new BinanceBookStream().Unsubscriptions(["ARB", "tia"]).Single().Should().Be("""{"method":"UNSUBSCRIBE","params":["arbusdt@depth20@100ms","tiausdt@depth20@100ms"],"id":1}""");
        new BinanceSpotBookStream().Subscriptions(["ARB"]).Single().Should().Be("""{"method":"SUBSCRIBE","params":["arbusdt@depth20@100ms"],"id":1}""");
        new HyperliquidBookStream().Unsubscriptions(["ARB"]).Single().Should().Be("""{"method":"unsubscribe","subscription":{"type":"l2Book","coin":"ARB"}}""");
        new KucoinSpotBookStream().Unsubscriptions(["ARB"]).Single().Should().Contain("\"type\":\"unsubscribe\"").And.Contain("ARB-USDT");
        new BitgetBookStream(InstrumentType.Perp).Unsubscriptions(["ARB"]).Single().Should().StartWith("""{"op":"unsubscribe",""");
        new GateSpotBookStream().Unsubscriptions(["ARB"]).Single().Should().Contain("\"event\":\"unsubscribe\"").And.Contain("ARB_USDT");
        new CoinbaseSpotBookStream().Unsubscriptions(["ARB"]).Single().Should().StartWith("""{"type":"unsubscribe","product_ids":["ARB-USD"]""");
        new KrakenSpotBookStream().Unsubscriptions(["ARB"]).Single().Should().StartWith("""{"method":"unsubscribe",""").And.Contain("ARB/USD");
        DeribitPerpBookStream deribit = new();
        deribit.UseListed(["ARB"]);
        deribit.Unsubscriptions(["ARB"]).Single().Should().Contain("\"method\":\"public/unsubscribe\"").And.Contain("book.ARB_USDC-PERPETUAL.none.20.100ms");
        MexcPerpBookStream mexc = new();
        mexc.UseContractSizes(new Dictionary<string, decimal> { ["ARB_USDT"] = 1m });
        mexc.Unsubscriptions(["ARB"]).Single().Should().StartWith("""{"method":"unsub.depth.full",""");
    }
}

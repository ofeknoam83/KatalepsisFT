using System.Globalization;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Infrastructure.Execution;

/// <summary>
/// One instrument as a venue lists it: our name for the asset, the venue's own symbol, whether it is open for orders,
/// and the smallest steps the venue takes in price and quantity. Quantities are in units of the asset, also where
/// the venue itself counts in contracts. A step the venue's list does not give is left empty.
/// </summary>
public sealed record ListedSymbol(string Asset, string Symbol, bool Tradable, decimal? PriceStep = null, decimal? QuantityStep = null, decimal? MinQuantity = null);

/// <summary>
/// Where one venue publishes the instruments of one kind, and how to read the answer. Only instruments whose books
/// this service can follow are kept: quoted in what the venue's stream is quoted in, and named the way the stream
/// names them. Which venues list what, and how each names things, was first worked out in Katalepsis
/// (MarketInfoService, PerpetualSymbolsService); the reading here is written against the venues' public answers.
/// </summary>
public sealed record InstrumentListing(Venue Venue, InstrumentType Type, string Quote, Func<HttpRequestMessage> Request, Func<JsonElement, IEnumerable<ListedSymbol>> Read)
{
    /// <summary>Reads a recorded or received answer. An asset the system cannot name (odd characters, too long) is left out.</summary>
    public IReadOnlyList<ListedSymbol> ReadAnswer(string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer);
        return Read(document.RootElement)
            .Where(s => IsAssetName(s.Asset))
            .GroupBy(s => s.Asset)
            // Listed twice under one name: open for orders wins.
            .Select(g => g.OrderByDescending(s => s.Tradable).First())
            .OrderBy(s => s.Asset, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Capital letters and digits only, as an order's asset must be.</summary>
    public static bool IsAssetName(string asset) => asset.Length is > 0 and <= 20 && asset.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');
}

/// <summary>Every venue's public list of instruments, for the kinds of market this service keeps books for.</summary>
public static class InstrumentListings
{
    public static readonly IReadOnlyList<InstrumentListing> All =
    [
        new(Venue.Binance, InstrumentType.Spot, "USDT", () => Get("https://data-api.binance.vision/api/v3/exchangeInfo?permissions=SPOT"), root =>
            from s in Items(root, "symbols")
            where Text(s, "quoteAsset") == "USDT" && Text(s, "symbol") == Text(s, "baseAsset") + "USDT"
            select new ListedSymbol(Text(s, "baseAsset"), Text(s, "symbol"), Text(s, "status") == "TRADING", Number(Filter(s, "PRICE_FILTER"), "tickSize"), Number(Filter(s, "LOT_SIZE"), "stepSize"), Number(Filter(s, "LOT_SIZE"), "minQty"))),
        new(Venue.Binance, InstrumentType.Perp, "USDT", () => Get("https://fapi.binance.com/fapi/v1/exchangeInfo"), root =>
            from s in Items(root, "symbols")
            where Text(s, "quoteAsset") == "USDT" && Text(s, "contractType") == "PERPETUAL" && Text(s, "symbol") == Text(s, "baseAsset") + "USDT"
            select new ListedSymbol(Text(s, "baseAsset"), Text(s, "symbol"), Text(s, "status") == "TRADING", Number(Filter(s, "PRICE_FILTER"), "tickSize"), Number(Filter(s, "LOT_SIZE"), "stepSize"), Number(Filter(s, "LOT_SIZE"), "minQty"))),
        new(Venue.Binance, InstrumentType.Perp, "USDC", () => Get("https://fapi.binance.com/fapi/v1/exchangeInfo"), root =>
            from s in Items(root, "symbols")
            where Text(s, "quoteAsset") == "USDC" && Text(s, "contractType") == "PERPETUAL" && Text(s, "symbol") == Text(s, "baseAsset") + "USDC"
            select new ListedSymbol(Text(s, "baseAsset"), Text(s, "symbol"), Text(s, "status") == "TRADING", Number(Filter(s, "PRICE_FILTER"), "tickSize"), Number(Filter(s, "LOT_SIZE"), "stepSize"), Number(Filter(s, "LOT_SIZE"), "minQty"))),
        new(Venue.Bybit, InstrumentType.Spot, "USDT", () => Get("https://api.bybit.com/v5/market/instruments-info?category=spot&limit=1000"), root =>
            from s in Items(root.GetProperty("result"), "list")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "symbol") == Text(s, "baseCoin") + "USDT"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Text(s, "status") == "Trading", Number(Part(s, "priceFilter"), "tickSize"), Number(Part(s, "lotSizeFilter"), "basePrecision"), Number(Part(s, "lotSizeFilter"), "minOrderQty"))),
        new(Venue.Bybit, InstrumentType.Perp, "USDT", () => Get("https://api.bybit.com/v5/market/instruments-info?category=linear&limit=1000"), root =>
            from s in Items(root.GetProperty("result"), "list")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "contractType") == "LinearPerpetual" && Text(s, "symbol") == Text(s, "baseCoin") + "USDT"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Text(s, "status") == "Trading", Number(Part(s, "priceFilter"), "tickSize"), Number(Part(s, "lotSizeFilter"), "qtyStep"), Number(Part(s, "lotSizeFilter"), "minOrderQty"))),
        new(Venue.Bybit, InstrumentType.Perp, "USDC", () => Get("https://api.bybit.com/v5/market/instruments-info?category=linear&limit=1000"), root =>
            from s in Items(root.GetProperty("result"), "list")
            // Bybit names its USDC perpetuals after the asset with PERP on the end.
            where Text(s, "quoteCoin") == "USDC" && Text(s, "contractType") == "LinearPerpetual" && Text(s, "symbol") == Text(s, "baseCoin") + "PERP"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Text(s, "status") == "Trading", Number(Part(s, "priceFilter"), "tickSize"), Number(Part(s, "lotSizeFilter"), "qtyStep"), Number(Part(s, "lotSizeFilter"), "minOrderQty"))),
        new(Venue.Okx, InstrumentType.Spot, "USDT", () => Get("https://www.okx.com/api/v5/public/instruments?instType=SPOT"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCcy") == "USDT" && Text(s, "instId") == Text(s, "baseCcy") + "-USDT"
            select new ListedSymbol(Text(s, "baseCcy"), Text(s, "instId"), Text(s, "state") == "live", Number(s, "tickSz"), Number(s, "lotSz"), Number(s, "minSz"))),
        new(Venue.Okx, InstrumentType.Perp, "USDT", () => Get("https://www.okx.com/api/v5/public/instruments?instType=SWAP"), root =>
            from s in Items(root, "data")
            let id = Text(s, "instId")
            where id.EndsWith("-USDT-SWAP", StringComparison.Ordinal)
            // OKX counts swaps in contracts; one contract is ctVal of the asset.
            select new ListedSymbol(id[..^"-USDT-SWAP".Length], id, Text(s, "state") == "live", Number(s, "tickSz"), Number(s, "lotSz") * Number(s, "ctVal"), Number(s, "minSz") * Number(s, "ctVal"))),
        new(Venue.Kucoin, InstrumentType.Spot, "USDT", () => Get("https://api.kucoin.com/api/v2/symbols"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCurrency") == "USDT" && Text(s, "symbol") == Text(s, "baseCurrency") + "-USDT"
            select new ListedSymbol(Text(s, "baseCurrency"), Text(s, "symbol"), Is(s, "enableTrading"), Number(s, "priceIncrement"), Number(s, "baseIncrement"), Number(s, "baseMinSize"))),
        new(Venue.Kucoin, InstrumentType.Perp, "USDT", () => Get("https://api-futures.kucoin.com/api/v1/contracts/active"), root =>
            from s in Items(root, "data")
            // KuCoin calls bitcoin XBT on its futures; everywhere else here it is BTC. Its contracts are a multiple of the asset.
            where Text(s, "quoteCurrency") == "USDT" && Text(s, "symbol").EndsWith("USDTM", StringComparison.Ordinal) && !Is(s, "isInverse")
            select new ListedSymbol(Text(s, "baseCurrency") == "XBT" ? "BTC" : Text(s, "baseCurrency"), Text(s, "symbol"), Text(s, "status") == "Open", Number(s, "tickSize"), Number(s, "lotSize") * Number(s, "multiplier"), Number(s, "lotSize") * Number(s, "multiplier"))),
        new(Venue.Bitget, InstrumentType.Spot, "USDT", () => Get("https://api.bitget.com/api/v2/spot/public/symbols"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "symbol") == Text(s, "baseCoin") + "USDT"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Text(s, "status") == "online", Places(s, "pricePrecision"), Places(s, "quantityPrecision"), Positive(Number(s, "minTradeAmount")))),
        new(Venue.Bitget, InstrumentType.Perp, "USDT", () => Get("https://api.bitget.com/api/v2/mix/market/contracts?productType=USDT-FUTURES"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "symbol") == Text(s, "baseCoin") + "USDT"
            // The price moves in priceEndStep units of its last decimal place.
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Text(s, "symbolStatus") == "normal", Places(s, "pricePlace") * Number(s, "priceEndStep"), Number(s, "sizeMultiplier"), Number(s, "minTradeNum"))),
        new(Venue.Gate, InstrumentType.Spot, "USDT", () => Get("https://api.gateio.ws/api/v4/spot/currency_pairs"), root =>
            from s in root.EnumerateArray()
            where Text(s, "quote") == "USDT" && Text(s, "id") == Text(s, "base") + "_USDT"
            select new ListedSymbol(Text(s, "base"), Text(s, "id"), Text(s, "trade_status") == "tradable", Places(s, "precision"), Places(s, "amount_precision"), Number(s, "min_base_amount"))),
        new(Venue.Gate, InstrumentType.Perp, "USDT", () => Get("https://api.gateio.ws/api/v4/futures/usdt/contracts"), root =>
            from s in root.EnumerateArray()
            let name = Text(s, "name")
            where name.EndsWith("_USDT", StringComparison.Ordinal)
            // Gate counts in contracts of quanto_multiplier of the asset each.
            select new ListedSymbol(name[..^"_USDT".Length], name, !Is(s, "in_delisting"), Number(s, "order_price_round"), Number(s, "quanto_multiplier"), Number(s, "order_size_min") * Number(s, "quanto_multiplier"))),
        new(Venue.Mexc, InstrumentType.Spot, "USDT", () => Get("https://api.mexc.com/api/v3/exchangeInfo"), root =>
            from s in Items(root, "symbols")
            where Text(s, "quoteAsset") == "USDT" && Text(s, "symbol") == Text(s, "baseAsset") + "USDT"
            select new ListedSymbol(Text(s, "baseAsset"), Text(s, "symbol"), Text(s, "status") is "1" or "ENABLED" && !IsFalse(s, "isSpotTradingAllowed"), Places(s, "quotePrecision"), Positive(Number(s, "baseSizePrecision")))),
        new(Venue.Mexc, InstrumentType.Perp, "USDT", () => Get("https://contract.mexc.com/api/v1/contract/detail"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "symbol") == Text(s, "baseCoin") + "_USDT"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Number(s, "state") == 0m, Number(s, "priceUnit"), Number(s, "volUnit") * Number(s, "contractSize"), Number(s, "minVol") * Number(s, "contractSize"))),
        new(Venue.CoinbaseExchange, InstrumentType.Spot, "USD", () => Get("https://api.exchange.coinbase.com/products"), root =>
            from s in root.EnumerateArray()
            where Text(s, "quote_currency") == "USD" && Text(s, "id") == Text(s, "base_currency") + "-USD"
            select new ListedSymbol(Text(s, "base_currency"), Text(s, "id"), Text(s, "status") == "online" && !Is(s, "trading_disabled"), Number(s, "quote_increment"), Number(s, "base_increment"))),
        new(Venue.Kraken, InstrumentType.Spot, "USD", () => Get("https://api.kraken.com/0/public/AssetPairs"), root =>
            from pair in root.GetProperty("result").EnumerateObject()
            let name = Text(pair.Value, "wsname")
            where name.EndsWith("/USD", StringComparison.Ordinal)
            // Kraken's older names for bitcoin and dogecoin; its stream takes the usual ones.
            let asset = name[..^"/USD".Length] switch { "XBT" => "BTC", "XDG" => "DOGE", var other => other }
            select new ListedSymbol(asset, asset + "/USD", Text(pair.Value, "status") == "online", Number(pair.Value, "tick_size"), Places(pair.Value, "lot_decimals"), Number(pair.Value, "ordermin"))),
        new(Venue.Deribit, InstrumentType.Perp, "USDC", () => Get("https://www.deribit.com/api/v2/public/get_instruments?currency=USDC&kind=future&expired=false"), root =>
            from s in Items(root, "result")
            let name = Text(s, "instrument_name")
            where name.EndsWith("_USDC-PERPETUAL", StringComparison.Ordinal)
            select new ListedSymbol(name[..^"_USDC-PERPETUAL".Length], name, Is(s, "is_active"), Number(s, "tick_size"), Number(s, "contract_size"), Number(s, "min_trade_amount"))),
        new(Venue.Hyperliquid, InstrumentType.Perp, "USDC",
            () => new HttpRequestMessage(HttpMethod.Post, "https://api.hyperliquid.xyz/info") { Content = new StringContent("""{"type":"meta"}""", Encoding.UTF8, "application/json") },
            root =>
                from s in Items(root, "universe")
                let name = Text(s, "name")
                select new ListedSymbol(HyperliquidBookStream.AssetOfCoin(name), name, !Is(s, "isDelisted"), null, Places(s, "szDecimals"), Places(s, "szDecimals"))),
    ];

    private static HttpRequestMessage Get(string address) => new(HttpMethod.Get, address);

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    private static JsonElement Part(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement part) ? part : default;

    /// <summary>One of Binance's rules for a symbol, by its kind.</summary>
    private static JsonElement Filter(JsonElement symbol, string kind) => Items(symbol, "filters").FirstOrDefault(f => Text(f, "filterType") == kind);

    private static string Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    private static bool Is(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static bool IsFalse(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.False;

    /// <summary>A figure the venue gives as a number or as text; empty when it is missing or not a figure.</summary>
    private static decimal? Number(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out decimal number) => number,
            JsonValueKind.Number when value.TryGetDouble(out double wide) && Math.Abs(wide) < 1e20 => (decimal)wide,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>The step that goes with a count of decimal places: 2 places is a step of 0.01.</summary>
    private static decimal? Places(JsonElement item, string name) =>
        Number(item, name) is decimal places && places is >= 0m and <= 18m && places == Math.Floor(places) ? 1m / (decimal)Math.Pow(10, (double)places) : null;

    private static decimal? Positive(decimal? value) => value > 0m ? value : null;
}

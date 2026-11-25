using System.Text;
using System.Text.Json;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Infrastructure.Execution;

/// <summary>One instrument as a venue lists it: our name for the asset, the venue's own symbol, and whether it is open for orders.</summary>
public sealed record ListedSymbol(string Asset, string Symbol, bool Tradable);

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
            select new ListedSymbol(Text(s, "baseAsset"), Text(s, "symbol"), Text(s, "status") == "TRADING")),
        new(Venue.Binance, InstrumentType.Perp, "USDT", () => Get("https://fapi.binance.com/fapi/v1/exchangeInfo"), root =>
            from s in Items(root, "symbols")
            where Text(s, "quoteAsset") == "USDT" && Text(s, "contractType") == "PERPETUAL" && Text(s, "symbol") == Text(s, "baseAsset") + "USDT"
            select new ListedSymbol(Text(s, "baseAsset"), Text(s, "symbol"), Text(s, "status") == "TRADING")),
        new(Venue.Bybit, InstrumentType.Spot, "USDT", () => Get("https://api.bybit.com/v5/market/instruments-info?category=spot&limit=1000"), root =>
            from s in Items(root.GetProperty("result"), "list")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "symbol") == Text(s, "baseCoin") + "USDT"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Text(s, "status") == "Trading")),
        new(Venue.Bybit, InstrumentType.Perp, "USDT", () => Get("https://api.bybit.com/v5/market/instruments-info?category=linear&limit=1000"), root =>
            from s in Items(root.GetProperty("result"), "list")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "contractType") == "LinearPerpetual" && Text(s, "symbol") == Text(s, "baseCoin") + "USDT"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Text(s, "status") == "Trading")),
        new(Venue.Okx, InstrumentType.Spot, "USDT", () => Get("https://www.okx.com/api/v5/public/instruments?instType=SPOT"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCcy") == "USDT" && Text(s, "instId") == Text(s, "baseCcy") + "-USDT"
            select new ListedSymbol(Text(s, "baseCcy"), Text(s, "instId"), Text(s, "state") == "live")),
        new(Venue.Okx, InstrumentType.Perp, "USDT", () => Get("https://www.okx.com/api/v5/public/instruments?instType=SWAP"), root =>
            from s in Items(root, "data")
            let id = Text(s, "instId")
            where id.EndsWith("-USDT-SWAP", StringComparison.Ordinal)
            select new ListedSymbol(id[..^"-USDT-SWAP".Length], id, Text(s, "state") == "live")),
        new(Venue.Kucoin, InstrumentType.Spot, "USDT", () => Get("https://api.kucoin.com/api/v2/symbols"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCurrency") == "USDT" && Text(s, "symbol") == Text(s, "baseCurrency") + "-USDT"
            select new ListedSymbol(Text(s, "baseCurrency"), Text(s, "symbol"), s.TryGetProperty("enableTrading", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True)),
        new(Venue.Kucoin, InstrumentType.Perp, "USDT", () => Get("https://api-futures.kucoin.com/api/v1/contracts/active"), root =>
            from s in Items(root, "data")
            // KuCoin calls bitcoin XBT on its futures; everywhere else here it is BTC. Dated contracts have an expiry.
            where Text(s, "quoteCurrency") == "USDT" && Text(s, "symbol").EndsWith("USDTM", StringComparison.Ordinal) && !(s.TryGetProperty("isInverse", out JsonElement inverse) && inverse.ValueKind == JsonValueKind.True)
            select new ListedSymbol(Text(s, "baseCurrency") == "XBT" ? "BTC" : Text(s, "baseCurrency"), Text(s, "symbol"), Text(s, "status") == "Open")),
        new(Venue.Bitget, InstrumentType.Spot, "USDT", () => Get("https://api.bitget.com/api/v2/spot/public/symbols"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "symbol") == Text(s, "baseCoin") + "USDT"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Text(s, "status") == "online")),
        new(Venue.Bitget, InstrumentType.Perp, "USDT", () => Get("https://api.bitget.com/api/v2/mix/market/contracts?productType=USDT-FUTURES"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "symbol") == Text(s, "baseCoin") + "USDT"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), Text(s, "symbolStatus") == "normal")),
        new(Venue.Gate, InstrumentType.Spot, "USDT", () => Get("https://api.gateio.ws/api/v4/spot/currency_pairs"), root =>
            from s in root.EnumerateArray()
            where Text(s, "quote") == "USDT" && Text(s, "id") == Text(s, "base") + "_USDT"
            select new ListedSymbol(Text(s, "base"), Text(s, "id"), Text(s, "trade_status") == "tradable")),
        new(Venue.Gate, InstrumentType.Perp, "USDT", () => Get("https://api.gateio.ws/api/v4/futures/usdt/contracts"), root =>
            from s in root.EnumerateArray()
            let name = Text(s, "name")
            where name.EndsWith("_USDT", StringComparison.Ordinal)
            select new ListedSymbol(name[..^"_USDT".Length], name, !(s.TryGetProperty("in_delisting", out JsonElement leaving) && leaving.ValueKind == JsonValueKind.True))),
        new(Venue.Mexc, InstrumentType.Spot, "USDT", () => Get("https://api.mexc.com/api/v3/exchangeInfo"), root =>
            from s in Items(root, "symbols")
            where Text(s, "quoteAsset") == "USDT" && Text(s, "symbol") == Text(s, "baseAsset") + "USDT"
            select new ListedSymbol(Text(s, "baseAsset"), Text(s, "symbol"), Text(s, "status") is "1" or "ENABLED" && !(s.TryGetProperty("isSpotTradingAllowed", out JsonElement allowed) && allowed.ValueKind == JsonValueKind.False))),
        new(Venue.Mexc, InstrumentType.Perp, "USDT", () => Get("https://contract.mexc.com/api/v1/contract/detail"), root =>
            from s in Items(root, "data")
            where Text(s, "quoteCoin") == "USDT" && Text(s, "symbol") == Text(s, "baseCoin") + "_USDT"
            select new ListedSymbol(Text(s, "baseCoin"), Text(s, "symbol"), s.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.Number && state.GetInt32() == 0)),
        new(Venue.CoinbaseExchange, InstrumentType.Spot, "USD", () => Get("https://api.exchange.coinbase.com/products"), root =>
            from s in root.EnumerateArray()
            where Text(s, "quote_currency") == "USD" && Text(s, "id") == Text(s, "base_currency") + "-USD"
            select new ListedSymbol(Text(s, "base_currency"), Text(s, "id"), Text(s, "status") == "online" && !(s.TryGetProperty("trading_disabled", out JsonElement off) && off.ValueKind == JsonValueKind.True))),
        new(Venue.Kraken, InstrumentType.Spot, "USD", () => Get("https://api.kraken.com/0/public/AssetPairs"), root =>
            from pair in root.GetProperty("result").EnumerateObject()
            let name = Text(pair.Value, "wsname")
            where name.EndsWith("/USD", StringComparison.Ordinal)
            // Kraken's older names for bitcoin and dogecoin; its stream takes the usual ones.
            let asset = name[..^"/USD".Length] switch { "XBT" => "BTC", "XDG" => "DOGE", var other => other }
            select new ListedSymbol(asset, asset + "/USD", Text(pair.Value, "status") == "online")),
        new(Venue.Deribit, InstrumentType.Perp, "USDC", () => Get("https://www.deribit.com/api/v2/public/get_instruments?currency=USDC&kind=future&expired=false"), root =>
            from s in Items(root, "result")
            let name = Text(s, "instrument_name")
            where name.EndsWith("_USDC-PERPETUAL", StringComparison.Ordinal)
            select new ListedSymbol(name[..^"_USDC-PERPETUAL".Length], name, s.TryGetProperty("is_active", out JsonElement active) && active.ValueKind == JsonValueKind.True)),
        new(Venue.Hyperliquid, InstrumentType.Perp, "USDC",
            () => new HttpRequestMessage(HttpMethod.Post, "https://api.hyperliquid.xyz/info") { Content = new StringContent("""{"type":"meta"}""", Encoding.UTF8, "application/json") },
            root =>
                from s in Items(root, "universe")
                // Names with small letters (kPEPE and the like) count in thousands; they are left out until that is handled.
                select new ListedSymbol(Text(s, "name"), Text(s, "name"), !(s.TryGetProperty("isDelisted", out JsonElement gone) && gone.ValueKind == JsonValueKind.True))),
    ];

    private static HttpRequestMessage Get(string address) => new(HttpMethod.Get, address);

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    private static string Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
}

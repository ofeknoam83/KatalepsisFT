using System.Globalization;
using System.Text.Json;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Infrastructure.Execution;

/// <summary>Binance spot against USDT. Every message is the whole top of one book, ten times a second.</summary>
public sealed class BinanceSpotBookStream : BookStream
{
    public override Venue Venue => Venue.Binance;

    public override InstrumentType Type => InstrumentType.Spot;

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://api.binance.com/api/v3/ping");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://stream.binance.com:9443/stream");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        assets.Count == 0 ? [] : [JsonSerializer.Serialize(new { method = "SUBSCRIBE", @params = assets.Select(a => a.ToLowerInvariant() + "usdt@depth20@100ms"), id = 1 })];

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        // Unlike the futures stream, the book itself does not say whose it is; the stream's name does.
        if (!root.TryGetProperty("stream", out JsonElement stream) || !root.TryGetProperty("data", out JsonElement data) || !data.TryGetProperty("bids", out JsonElement bids) || !data.TryGetProperty("asks", out JsonElement asks))
        {
            return null;
        }

        string name = stream.GetString()!;
        int at = name.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && AssetOf(name[..at]) is { } asset ? new StreamedBook(asset, Levels(bids), Levels(asks)) : null;
    }
}

/// <summary>Bybit spot against USDT: the same stream as its perpetuals, on the spot address.</summary>
public sealed class BybitSpotBookStream : BybitBookStream
{
    public override InstrumentType Type => InstrumentType.Spot;

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://stream.bybit.com/v5/public/spot");
}

/// <summary>OKX spot against USDT: the best five prices a side, whole, when they change. Sizes are in units of the asset.</summary>
public sealed class OkxSpotBookStream : BookStream
{
    public override Venue Venue => Venue.Okx;

    public override InstrumentType Type => InstrumentType.Spot;

    public override string? Ping => "ping";

    public override bool SilenceMeansUnchanged => true;

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://www.okx.com/api/v5/public/time");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://ws.okx.com:8443/ws/v5/public");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        [JsonSerializer.Serialize(new { op = "subscribe", args = assets.Select(a => new { channel = "books5", instId = $"{a.ToUpperInvariant()}-USDT" }) })];

    public override StreamedBook? Read(string message)
    {
        if (message.Length < 8)
        {
            // "pong"
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("data", out JsonElement data) || !root.TryGetProperty("arg", out JsonElement arg) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return null;
        }

        string instrument = arg.GetProperty("instId").GetString()!;
        return instrument.EndsWith("-USDT", StringComparison.Ordinal) ? new StreamedBook(instrument[..^5], Levels(data[0].GetProperty("bids")), Levels(data[0].GetProperty("asks"))) : null;
    }
}

/// <summary>
/// Bitget, spot or USDT-margined perpetuals: the best fifteen prices a side, whole, when they change. Sizes are in
/// units of the asset on both.
/// </summary>
public sealed class BitgetBookStream(InstrumentType type) : BookStream
{
    private string Family => type == InstrumentType.Spot ? "SPOT" : "USDT-FUTURES";

    public override Venue Venue => Venue.Bitget;

    public override InstrumentType Type => type;

    public override string? Ping => "ping";

    public override bool SilenceMeansUnchanged => true;

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://api.bitget.com/api/v2/public/time");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://ws.bitget.com/v2/ws/public");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        [JsonSerializer.Serialize(new { op = "subscribe", args = assets.Select(a => new { instType = Family, channel = "books15", instId = a.ToUpperInvariant() + "USDT" }) })];

    public override StreamedBook? Read(string message)
    {
        if (message.Length < 8)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("data", out JsonElement data) || !root.TryGetProperty("arg", out JsonElement arg) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0 || arg.GetProperty("instType").GetString() != Family)
        {
            return null;
        }

        return AssetOf(arg.GetProperty("instId").GetString()!) is { } asset ? new StreamedBook(asset, Levels(data[0].GetProperty("bids")), Levels(data[0].GetProperty("asks"))) : null;
    }
}

/// <summary>Gate spot against USDT: the best twenty prices a side, whole, ten times a second while they change.</summary>
public sealed class GateSpotBookStream : BookStream
{
    public override Venue Venue => Venue.Gate;

    public override InstrumentType Type => InstrumentType.Spot;

    public override bool SilenceMeansUnchanged => true;

    public override string? Ping => $$"""{"time":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"channel":"spot.ping"}""";

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://api.gateio.ws/api/v4/spot/time");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://api.gateio.ws/ws/v4/");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        assets.Select(a => JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), channel = "spot.order_book", @event = "subscribe", payload = new[] { $"{a.ToUpperInvariant()}_USDT", "20", "100ms" } }));

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("channel", out JsonElement channel) || channel.GetString() != "spot.order_book" || !root.TryGetProperty("event", out JsonElement kind) || kind.GetString() != "update" || !root.TryGetProperty("result", out JsonElement result))
        {
            return null;
        }

        string pair = result.GetProperty("s").GetString()!;
        return pair.EndsWith("_USDT", StringComparison.Ordinal) ? new StreamedBook(pair[..^5], Levels(result.GetProperty("bids")), Levels(result.GetProperty("asks"))) : null;
    }
}

/// <summary>
/// What Gate's and MEXC's perpetual streams share: sizes come in contracts, so the size of each contract is read
/// first and every size turned into units of the asset; and every message is the whole top of one book.
/// </summary>
public abstract class ContractBookStream : BookStream
{
    /// <summary>The size of one contract of each perpetual that is followed, by the venue's name for it ("BTC_USDT").</summary>
    protected Dictionary<string, decimal> ContractSizes { get; private set; } = [];

    public override bool SilenceMeansUnchanged => true;

    protected abstract Uri ContractsAddress { get; }

    public override async Task PrepareAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(ContractsAddress, cancellationToken);
        response.EnsureSuccessStatusCode();
        UseContractSizes(ReadContractSizes(await response.Content.ReadAsStringAsync(cancellationToken)));
    }

    public abstract Dictionary<string, decimal> ReadContractSizes(string contracts);

    public void UseContractSizes(Dictionary<string, decimal> sizes) => ContractSizes = sizes;

    protected IEnumerable<string> Followed(IReadOnlyList<string> assets) => assets.Select(a => $"{a.ToUpperInvariant()}_USDT").Where(ContractSizes.ContainsKey);

    protected StreamedBook? Book(string contract, List<BookLevel> bids, List<BookLevel> asks) =>
        contract.EndsWith("_USDT", StringComparison.Ordinal) && ContractSizes.ContainsKey(contract) ? new StreamedBook(contract[..^5], bids, asks) : null;

    protected static decimal Value(JsonElement value) => value.ValueKind == JsonValueKind.String ? decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture) : value.GetDecimal();
}

/// <summary>Gate USDT-margined perpetuals.</summary>
public sealed class GatePerpBookStream : ContractBookStream
{
    public override Venue Venue => Venue.Gate;

    public override string? Ping => $$"""{"time":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"channel":"futures.ping"}""";

    protected override Uri ContractsAddress => new("https://api.gateio.ws/api/v4/futures/usdt/contracts");

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://api.gateio.ws/api/v4/spot/time");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://fx-ws.gateio.ws/v4/ws/usdt");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        Followed(assets).Select(c => JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), channel = "futures.order_book", @event = "subscribe", payload = new[] { c, "20", "0" } }));

    public override Dictionary<string, decimal> ReadContractSizes(string contracts)
    {
        using JsonDocument document = JsonDocument.Parse(contracts);
        Dictionary<string, decimal> sizes = [];
        foreach (JsonElement contract in document.RootElement.EnumerateArray())
        {
            bool leaving = contract.TryGetProperty("in_delisting", out JsonElement delisting) && delisting.ValueKind == JsonValueKind.True;
            bool trading = !contract.TryGetProperty("status", out JsonElement status) || status.GetString() == "trading";
            if (!leaving && trading && decimal.TryParse(contract.GetProperty("quanto_multiplier").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal size) && size > 0m)
            {
                sizes[contract.GetProperty("name").GetString()!] = size;
            }
        }

        return sizes;
    }

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("channel", out JsonElement channel) || channel.GetString() != "futures.order_book" || !root.TryGetProperty("event", out JsonElement kind) || kind.GetString() != "all" || !root.TryGetProperty("result", out JsonElement result))
        {
            return null;
        }

        string contract = result.GetProperty("contract").GetString()!;
        return ContractSizes.TryGetValue(contract, out decimal size) ? Book(contract, Side(result.GetProperty("bids"), size), Side(result.GetProperty("asks"), size)) : null;
    }

    /// <summary>Levels given as { "p": price as text, "s": contracts }.</summary>
    private static List<BookLevel> Side(JsonElement side, decimal contractSize)
    {
        List<BookLevel> levels = new(Depth);
        foreach (JsonElement level in side.EnumerateArray())
        {
            decimal quantity = Math.Abs(Value(level.GetProperty("s"))) * contractSize;
            if (quantity > 0m && levels.Count < Depth)
            {
                levels.Add(new BookLevel(Value(level.GetProperty("p")), quantity));
            }
        }

        return levels;
    }
}

/// <summary>MEXC USDT-margined perpetuals.</summary>
public sealed class MexcPerpBookStream : ContractBookStream
{
    public override Venue Venue => Venue.Mexc;

    public override string? Ping => """{"method":"ping"}""";

    protected override Uri ContractsAddress => new("https://contract.mexc.com/api/v1/contract/detail");

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://contract.mexc.com/api/v1/contract/ping");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://contract.mexc.com/edge");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        Followed(assets).Select(c => JsonSerializer.Serialize(new { method = "sub.depth.full", param = new { symbol = c, limit = 20 } }));

    public override Dictionary<string, decimal> ReadContractSizes(string contracts)
    {
        using JsonDocument document = JsonDocument.Parse(contracts);
        Dictionary<string, decimal> sizes = [];
        foreach (JsonElement contract in document.RootElement.GetProperty("data").EnumerateArray())
        {
            // State 0 is a contract that is trading.
            if (contract.GetProperty("settleCoin").GetString() == "USDT" && contract.GetProperty("state").GetInt32() == 0 && Value(contract.GetProperty("contractSize")) is > 0m and decimal size)
            {
                sizes[contract.GetProperty("symbol").GetString()!] = size;
            }
        }

        return sizes;
    }

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("symbol", out JsonElement symbol) || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("bids", out JsonElement bids) || !data.TryGetProperty("asks", out JsonElement asks))
        {
            return null;
        }

        if (root.TryGetProperty("channel", out JsonElement channel) && channel.GetString() != "push.depth.full")
        {
            return null;
        }

        string contract = symbol.GetString()!;
        return ContractSizes.TryGetValue(contract, out decimal size) ? Book(contract, Side(bids, size), Side(asks, size)) : null;
    }

    /// <summary>Levels given as [price, contracts, number of orders], as numbers.</summary>
    private static List<BookLevel> Side(JsonElement side, decimal contractSize)
    {
        List<BookLevel> levels = new(Depth);
        foreach (JsonElement level in side.EnumerateArray())
        {
            decimal quantity = Value(level[1]) * contractSize;
            if (quantity > 0m && levels.Count < Depth)
            {
                levels.Add(new BookLevel(Value(level[0]), quantity));
            }
        }

        return levels;
    }
}

/// <summary>A book kept from a first full copy and the changes after it. A quantity of zero takes a price away.</summary>
public sealed class KeptBook
{
    private readonly SortedDictionary<decimal, decimal> _bids = [];
    private readonly SortedDictionary<decimal, decimal> _asks = [];

    public void Clear()
    {
        _bids.Clear();
        _asks.Clear();
    }

    public void Set(bool bid, decimal price, decimal quantity)
    {
        SortedDictionary<decimal, decimal> side = bid ? _bids : _asks;
        if (quantity <= 0m)
        {
            side.Remove(price);
        }
        else
        {
            side[price] = quantity;
        }
    }

    /// <summary>Drops everything beyond the best so many prices a side, as venues that send only that many expect.</summary>
    public void KeepBest(int depth)
    {
        while (_bids.Count > depth)
        {
            _bids.Remove(_bids.First().Key);
        }

        while (_asks.Count > depth)
        {
            _asks.Remove(_asks.Last().Key);
        }
    }

    public StreamedBook Top(string asset, int depth) =>
        new(asset, _bids.Reverse().Take(depth).Select(l => new BookLevel(l.Key, l.Value)).ToList(), _asks.Take(depth).Select(l => new BookLevel(l.Key, l.Value)).ToList());
}

/// <summary>
/// Coinbase Exchange spot against the dollar. The first message for a book is all of it; later ones are the prices
/// that changed, in batches.
/// </summary>
public sealed class CoinbaseSpotBookStream : BookStream
{
    private readonly Dictionary<string, KeptBook> _books = [];

    public override Venue Venue => Venue.CoinbaseExchange;

    public override InstrumentType Type => InstrumentType.Spot;

    public override string Quote => "USD";

    public override bool SilenceMeansUnchanged => true;

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://api.exchange.coinbase.com/time") { Headers = { { "User-Agent", "kft" } } };

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://ws-feed.exchange.coinbase.com");

    // The heartbeat keeps something arriving every second, so a quiet book is told apart from a dead connection.
    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        [JsonSerializer.Serialize(new { type = "subscribe", product_ids = assets.Select(a => $"{a.ToUpperInvariant()}-USD"), channels = new[] { "level2_batch", "heartbeat" } })];

    public override void Reset() => _books.Clear();

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        string? kind = root.TryGetProperty("type", out JsonElement type) ? type.GetString() : null;
        if (kind is not ("snapshot" or "l2update") || !root.TryGetProperty("product_id", out JsonElement product))
        {
            return null;
        }

        string pair = product.GetString()!;
        if (!pair.EndsWith("-USD", StringComparison.Ordinal))
        {
            return null;
        }

        if (kind == "snapshot")
        {
            KeptBook whole = _books[pair] = new KeptBook();
            foreach (JsonElement level in root.GetProperty("bids").EnumerateArray())
            {
                whole.Set(true, Number(level[0]), Number(level[1]));
            }

            foreach (JsonElement level in root.GetProperty("asks").EnumerateArray())
            {
                whole.Set(false, Number(level[0]), Number(level[1]));
            }

            return whole.Top(pair[..^4], Depth);
        }

        if (!_books.TryGetValue(pair, out KeptBook? book))
        {
            return null;
        }

        foreach (JsonElement change in root.GetProperty("changes").EnumerateArray())
        {
            book.Set(change[0].GetString() == "buy", Number(change[1]), Number(change[2]));
        }

        return book.Top(pair[..^4], Depth);
    }
}

/// <summary>
/// Kraken spot against the dollar: the best twenty-five prices a side, first whole and then as changes. Kraken
/// expects a reader to drop whatever falls out of the best twenty-five when a better price comes in.
/// </summary>
public sealed class KrakenSpotBookStream : BookStream
{
    private const int Kept = 25;
    private readonly Dictionary<string, KeptBook> _books = [];

    public override Venue Venue => Venue.Kraken;

    public override InstrumentType Type => InstrumentType.Spot;

    public override string Quote => "USD";

    public override bool SilenceMeansUnchanged => true;

    public override string? Ping => """{"method":"ping"}""";

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://api.kraken.com/0/public/Time");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://ws.kraken.com/v2");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        [JsonSerializer.Serialize(new { method = "subscribe", @params = new { channel = "book", symbol = assets.Select(a => $"{a.ToUpperInvariant()}/USD"), depth = Kept } })];

    public override void Reset() => _books.Clear();

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("channel", out JsonElement channel) || channel.GetString() != "book" || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return null;
        }

        bool whole = root.GetProperty("type").GetString() == "snapshot";
        JsonElement sent = data[0];
        string pair = sent.GetProperty("symbol").GetString()!;
        if (!pair.EndsWith("/USD", StringComparison.Ordinal))
        {
            return null;
        }

        if (whole)
        {
            _books[pair] = new KeptBook();
        }

        if (!_books.TryGetValue(pair, out KeptBook? book))
        {
            return null;
        }

        foreach (JsonElement level in sent.GetProperty("bids").EnumerateArray())
        {
            book.Set(true, level.GetProperty("price").GetDecimal(), level.GetProperty("qty").GetDecimal());
        }

        foreach (JsonElement level in sent.GetProperty("asks").EnumerateArray())
        {
            book.Set(false, level.GetProperty("price").GetDecimal(), level.GetProperty("qty").GetDecimal());
        }

        book.KeepBest(Kept);
        return book.Top(pair[..^4], Depth);
    }
}

/// <summary>
/// Deribit's perpetuals that are settled in USDC and sized in units of the asset. (Its older bitcoin and ether
/// perpetuals are sized in dollars and pay out in the coin; those are a different kind of contract and are not here.)
/// Every message is the best twenty prices a side, whole.
/// </summary>
public sealed class DeribitPerpBookStream : BookStream
{
    private const string Suffix = "_USDC-PERPETUAL";
    private HashSet<string> _listed = [];

    public override Venue Venue => Venue.Deribit;

    public override string Quote => "USDC";

    public override bool SilenceMeansUnchanged => true;

    public override string? Ping => """{"jsonrpc":"2.0","id":9,"method":"public/test","params":{}}""";

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://www.deribit.com/api/v2/public/test");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://www.deribit.com/ws/api/v2");

    public override async Task PrepareAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync("https://www.deribit.com/api/v2/public/get_instruments?currency=USDC&kind=future&expired=false", cancellationToken);
        response.EnsureSuccessStatusCode();
        UseListed(ReadListed(await response.Content.ReadAsStringAsync(cancellationToken)));
    }

    /// <summary>The assets Deribit has such a perpetual for.</summary>
    public static HashSet<string> ReadListed(string instruments)
    {
        using JsonDocument document = JsonDocument.Parse(instruments);
        HashSet<string> listed = [];
        foreach (JsonElement instrument in document.RootElement.GetProperty("result").EnumerateArray())
        {
            string name = instrument.GetProperty("instrument_name").GetString()!;
            bool linear = !instrument.TryGetProperty("instrument_type", out JsonElement kind) || kind.GetString() == "linear";
            bool active = !instrument.TryGetProperty("is_active", out JsonElement on) || on.ValueKind != JsonValueKind.False;
            if (name.EndsWith(Suffix, StringComparison.Ordinal) && linear && active)
            {
                listed.Add(name[..^Suffix.Length]);
            }
        }

        return listed;
    }

    public void UseListed(HashSet<string> listed) => _listed = listed;

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets)
    {
        List<string> channels = assets.Select(a => a.ToUpperInvariant()).Where(_listed.Contains).Select(a => $"book.{a}{Suffix}.none.20.100ms").ToList();
        return channels.Count == 0 ? [] : [JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "public/subscribe", @params = new { channels } })];
    }

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("method", out JsonElement method) || method.GetString() != "subscription" || !root.TryGetProperty("params", out JsonElement sent) || !sent.TryGetProperty("data", out JsonElement data))
        {
            return null;
        }

        string name = data.GetProperty("instrument_name").GetString()!;
        return name.EndsWith(Suffix, StringComparison.Ordinal) ? new StreamedBook(name[..^Suffix.Length], Side(data.GetProperty("bids")), Side(data.GetProperty("asks"))) : null;
    }

    /// <summary>Levels given as [price, amount], as numbers.</summary>
    private static List<BookLevel> Side(JsonElement side)
    {
        List<BookLevel> levels = new(Depth);
        foreach (JsonElement level in side.EnumerateArray())
        {
            decimal quantity = level[1].GetDecimal();
            if (quantity > 0m && levels.Count < Depth)
            {
                levels.Add(new BookLevel(level[0].GetDecimal(), quantity));
            }
        }

        return levels;
    }
}

/// <summary>Binance perpetuals margined in USDC: the same stream as the USDT ones, under symbols that end in USDC.</summary>
public sealed class BinanceUsdcPerpBookStream : BinanceBookStream
{
    public override string Quote => "USDC";
}

/// <summary>
/// Bybit perpetuals margined in USDC. They share the linear stream with the USDT ones; Bybit names them after the
/// asset with PERP on the end (BTCPERP).
/// </summary>
public sealed class BybitUsdcPerpBookStream : BybitBookStream
{
    public override string Quote => "USDC";

    protected override string Ending => "PERP";
}

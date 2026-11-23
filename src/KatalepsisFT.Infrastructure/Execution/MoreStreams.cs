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

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://stream.binance.com:9443/stream?streams=" + string.Join('/', assets.Select(a => a.ToLowerInvariant() + "usdt@depth20@100ms")));

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

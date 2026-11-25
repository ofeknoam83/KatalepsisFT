using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using Microsoft.Extensions.Logging;

namespace KatalepsisFT.Infrastructure.Execution;

/// <summary>A book a venue's message completed: the asset and both sides, best prices first.</summary>
public sealed record StreamedBook(string Asset, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks);

/// <summary>
/// What is particular to one venue's market data stream for perpetuals: where it is, what to ask for, how to keep it
/// open, and how to read a message. Reading is kept apart from the connection so it can be tested on recorded messages.
/// </summary>
public abstract class BookStream
{
    /// <summary>How many prices a side are kept. Enough to price any order the limits allow on the liquid names.</summary>
    public const int Depth = 20;

    public abstract Venue Venue { get; }

    /// <summary>What kind of instrument this stream's books are for.</summary>
    public virtual InstrumentType Type => InstrumentType.Perp;

    /// <summary>What this stream's prices are in.</summary>
    public virtual string Quote => "USDT";

    /// <summary>A request whose round trip is timed, as a measure of how long an order would take to reach the venue and be answered.</summary>
    public abstract HttpRequestMessage TimingRequest();

    public abstract Uri Address(IReadOnlyList<string> assets);

    /// <summary>What to send once connected to start receiving books.</summary>
    public virtual IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) => [];

    /// <summary>
    /// What to send to stop receiving the books of these assets. Every venue here undoes a request with the same
    /// message under the opposite name, so the request is rewritten; a venue that differs overrides this.
    /// </summary>
    public virtual IEnumerable<string> Unsubscriptions(IReadOnlyList<string> assets) =>
        Subscriptions(assets).Select(s => s
            .Replace("\"subscribe\"", "\"unsubscribe\"", StringComparison.Ordinal)
            .Replace("\"SUBSCRIBE\"", "\"UNSUBSCRIBE\"", StringComparison.Ordinal)
            .Replace("public/subscribe\"", "public/unsubscribe\"", StringComparison.Ordinal)
            .Replace("\"sub.depth.full\"", "\"unsub.depth.full\"", StringComparison.Ordinal));

    /// <summary>What to send now and then so the venue keeps the connection open; null when the venue does that itself.</summary>
    public virtual string? Ping => null;

    /// <summary>
    /// True when the venue only sends a book when it changes. Then a book that has not been sent again is still
    /// good for as long as the connection keeps talking. False when the venue sends every book again on a clock:
    /// there, a book that stops arriving is a book that can no longer be trusted.
    /// </summary>
    public virtual bool SilenceMeansUnchanged => false;

    /// <summary>
    /// Whether this stream can ask for the asset's book with what it learnt in <see cref="PrepareAsync"/>. A stream
    /// that needs a contract size or the venue's own symbol cannot follow an instrument listed after it last looked.
    /// </summary>
    public virtual bool CanFollow(string asset) => true;

    /// <summary>How many books are asked for in one message. Some venues refuse a longer list; some limit how many messages they take a second.</summary>
    public virtual int AskTogether => 10;

    /// <summary>Anything to learn before connecting, such as contract sizes.</summary>
    public virtual Task PrepareAsync(HttpClient http, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Forgets what earlier messages built up. Called before every new connection.</summary>
    public virtual void Reset() { }

    /// <summary>Reads one message. Returns the book it completes, or null when the message is not a book.</summary>
    public abstract StreamedBook? Read(string message);

    protected static decimal Number(JsonElement value) => decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Levels given as [price, size, ...] arrays of text, already in order.</summary>
    protected static List<BookLevel> Levels(JsonElement side, decimal sizeFactor = 1m)
    {
        List<BookLevel> levels = new(Depth);
        foreach (JsonElement level in side.EnumerateArray())
        {
            decimal quantity = Number(level[1]) * sizeFactor;
            if (quantity > 0m && levels.Count < Depth)
            {
                levels.Add(new BookLevel(Number(level[0]), quantity));
            }
        }

        return levels;
    }

    /// <summary>"BTCUSDT" to "BTC"; null for a symbol that is not quoted in USDT.</summary>
    protected static string? AssetOf(string symbol) => symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) && symbol.Length > 4 ? symbol[..^4].ToUpperInvariant() : null;
}

/// <summary>Binance USDT perpetuals. Every message is the whole top of one book, ten times a second.</summary>
public class BinanceBookStream : BookStream
{
    public override Venue Venue => Venue.Binance;

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://fapi.binance.com/fapi/v1/ping");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://fstream.binance.com/stream");

    // Binance takes hundreds of streams in one request but only a few requests a second.
    public override int AskTogether => 150;

    // Asked for in a message, not in the address, so that books can be added and dropped on an open connection.
    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        assets.Count == 0 ? [] : [JsonSerializer.Serialize(new { method = "SUBSCRIBE", @params = assets.Select(a => (a + Quote).ToLowerInvariant() + "@depth20@100ms"), id = 1 })];

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement data = document.RootElement.TryGetProperty("data", out JsonElement inner) ? inner : document.RootElement;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("s", out JsonElement symbol) || !data.TryGetProperty("b", out JsonElement bids) || !data.TryGetProperty("a", out JsonElement asks))
        {
            return null;
        }

        string name = symbol.GetString()!;
        return name.EndsWith(Quote, StringComparison.OrdinalIgnoreCase) && name.Length > Quote.Length ? new StreamedBook(name[..^Quote.Length].ToUpperInvariant(), Levels(bids), Levels(asks)) : null;
    }
}

/// <summary>
/// Bybit USDT perpetuals. The first message for a book is all of it; every later one is only what changed, so the book
/// is kept here and the changes applied to it. A size of zero means the price is gone.
/// </summary>
public class BybitBookStream : BookStream
{
    private readonly Dictionary<string, (SortedDictionary<decimal, decimal> Bids, SortedDictionary<decimal, decimal> Asks)> _books = [];

    public override Venue Venue => Venue.Bybit;

    public override string? Ping => """{"op":"ping"}""";

    public override bool SilenceMeansUnchanged => true;

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://api.bybit.com/v5/market/time");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://stream.bybit.com/v5/public/linear");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        [JsonSerializer.Serialize(new { op = "subscribe", args = assets.Select(a => $"orderbook.50.{a.ToUpperInvariant()}{Ending}") })];

    /// <summary>What comes after the asset in this market's symbols.</summary>
    protected virtual string Ending => "USDT";

    public override void Reset() => _books.Clear();

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("topic", out JsonElement topic) || !topic.GetString()!.StartsWith("orderbook.", StringComparison.Ordinal) || !root.TryGetProperty("data", out JsonElement data))
        {
            return null;
        }

        string symbol = data.GetProperty("s").GetString()!;
        if (!symbol.EndsWith(Ending, StringComparison.Ordinal) || symbol.Length == Ending.Length)
        {
            return null;
        }

        string asset = symbol[..^Ending.Length];
        bool whole = root.TryGetProperty("type", out JsonElement type) && type.GetString() == "snapshot";
        if (whole || !_books.TryGetValue(symbol, out var book))
        {
            if (!whole)
            {
                // A change for a book never received in full cannot be applied to anything.
                return null;
            }

            _books[symbol] = book = (new SortedDictionary<decimal, decimal>(), new SortedDictionary<decimal, decimal>());
        }

        Apply(book.Bids, data.GetProperty("b"));
        Apply(book.Asks, data.GetProperty("a"));
        return new StreamedBook(asset, book.Bids.Reverse().Take(Depth).Select(l => new BookLevel(l.Key, l.Value)).ToList(), book.Asks.Take(Depth).Select(l => new BookLevel(l.Key, l.Value)).ToList());
    }

    private static void Apply(SortedDictionary<decimal, decimal> side, JsonElement changes)
    {
        foreach (JsonElement change in changes.EnumerateArray())
        {
            decimal price = Number(change[0]);
            decimal quantity = Number(change[1]);
            if (quantity == 0m)
            {
                side.Remove(price);
            }
            else
            {
                side[price] = quantity;
            }
        }
    }
}

/// <summary>
/// OKX USDT perpetual swaps: the best five prices a side, whole, ten times a second. OKX gives sizes in contracts, so
/// the size of each contract is read first and every size turned into units of the asset.
/// </summary>
public sealed class OkxBookStream : BookStream
{
    private Dictionary<string, decimal> _contractSizes = [];

    public override Venue Venue => Venue.Okx;

    public override string? Ping => "ping";

    public override bool SilenceMeansUnchanged => true;

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, "https://www.okx.com/api/v5/public/time");

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://ws.okx.com:8443/ws/v5/public");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        [JsonSerializer.Serialize(new { op = "subscribe", args = assets.Where(a => _contractSizes.ContainsKey(InstrumentOf(a))).Select(a => new { channel = "books5", instId = InstrumentOf(a) }) })];

    public override async Task PrepareAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync("https://www.okx.com/api/v5/public/instruments?instType=SWAP", cancellationToken);
        response.EnsureSuccessStatusCode();
        _contractSizes = ReadContractSizes(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>The size of one contract of every USDT swap, in units of its asset.</summary>
    public static Dictionary<string, decimal> ReadContractSizes(string instruments)
    {
        using JsonDocument document = JsonDocument.Parse(instruments);
        Dictionary<string, decimal> sizes = [];
        foreach (JsonElement instrument in document.RootElement.GetProperty("data").EnumerateArray())
        {
            string id = instrument.GetProperty("instId").GetString()!;
            if (id.EndsWith("-USDT-SWAP", StringComparison.Ordinal) && decimal.TryParse(instrument.GetProperty("ctVal").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal size) && size > 0m)
            {
                sizes[id] = size;
            }
        }

        return sizes;
    }

    public void UseContractSizes(Dictionary<string, decimal> sizes) => _contractSizes = sizes;

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
        if (!_contractSizes.TryGetValue(instrument, out decimal contract))
        {
            return null;
        }

        JsonElement book = data[0];
        return new StreamedBook(instrument[..instrument.IndexOf('-', StringComparison.Ordinal)], Levels(book.GetProperty("bids"), contract), Levels(book.GetProperty("asks"), contract));
    }

    private static string InstrumentOf(string asset) => $"{asset.ToUpperInvariant()}-USDT-SWAP";

    public override bool CanFollow(string asset) => _contractSizes.ContainsKey(InstrumentOf(asset));
}

/// <summary>Hyperliquid perpetuals. Every message is the whole top of one book, about twice a second.</summary>
public sealed class HyperliquidBookStream : BookStream
{
    public override Venue Venue => Venue.Hyperliquid;

    // Hyperliquid's perpetuals are margined and quoted in USDC, and that is how its list is kept.
    public override string Quote => "USDC";

    public override string? Ping => """{"method":"ping"}""";

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Post, "https://api.hyperliquid.xyz/info") { Content = new StringContent("""{"type":"exchangeStatus"}""", Encoding.UTF8, "application/json") };

    public override Uri Address(IReadOnlyList<string> assets) => new("wss://api.hyperliquid.xyz/ws");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets) =>
        assets.Select(a => JsonSerializer.Serialize(new { method = "subscribe", subscription = new { type = "l2Book", coin = CoinOfAsset(a.ToUpperInvariant()) } }));

    /// <summary>
    /// Hyperliquid quotes some small coins a thousand at a time and marks them with a small k: kPEPE is a thousand
    /// PEPE. Other venues call the same thing 1000PEPE, and so does this system.
    /// </summary>
    public static string AssetOfCoin(string coin) => coin.Length > 1 && coin[0] == 'k' && coin[1..].All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9') ? "1000" + coin[1..] : coin;

    public static string CoinOfAsset(string asset) => asset.StartsWith("1000", StringComparison.Ordinal) && asset.Length > 4 ? "k" + asset[4..] : asset;

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("channel", out JsonElement channel) || channel.GetString() != "l2Book" || !root.TryGetProperty("data", out JsonElement data))
        {
            return null;
        }

        JsonElement levels = data.GetProperty("levels");
        return new StreamedBook(AssetOfCoin(data.GetProperty("coin").GetString()!).ToUpperInvariant(), Side(levels[0]), Side(levels[1]));
    }

    private static List<BookLevel> Side(JsonElement side)
    {
        List<BookLevel> levels = new(Depth);
        foreach (JsonElement level in side.EnumerateArray())
        {
            decimal quantity = Number(level.GetProperty("sz"));
            if (quantity > 0m && levels.Count < Depth)
            {
                levels.Add(new BookLevel(Number(level.GetProperty("px")), quantity));
            }
        }

        return levels;
    }
}

/// <summary>
/// What KuCoin's spot and futures streams share: the address of the stream and a short-lived token for it are asked
/// for first, and every message is the whole top of one book, about ten times a second while it changes.
/// </summary>
public abstract class KucoinBookStream : BookStream
{
    private Uri? _address;
    private int _ping;

    public override Venue Venue => Venue.Kucoin;

    public override bool SilenceMeansUnchanged => true;

    public override string? Ping => $$"""{"id":"p{{Interlocked.Increment(ref _ping)}}","type":"ping"}""";

    protected abstract string RestHost { get; }

    protected abstract string Topic { get; }

    /// <summary>The venue's name for the instrument of an asset, or null when it is not followed.</summary>
    protected abstract string? SymbolOf(string asset);

    public override HttpRequestMessage TimingRequest() => new(HttpMethod.Get, $"https://{RestHost}/api/v1/timestamp");

    public override async Task PrepareAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.PostAsync($"https://{RestHost}/api/v1/bullet-public", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        _address = ReadAddress(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>The stream's address with its token, from KuCoin's answer to "where do I connect".</summary>
    public static Uri ReadAddress(string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement data = document.RootElement.GetProperty("data");
        string endpoint = data.GetProperty("instanceServers")[0].GetProperty("endpoint").GetString()!;
        return new Uri($"{endpoint}{(endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?")}token={Uri.EscapeDataString(data.GetProperty("token").GetString()!)}");
    }

    public override Uri Address(IReadOnlyList<string> assets) => _address ?? throw new InvalidOperationException("The stream's address has not been asked for.");

    public override IEnumerable<string> Subscriptions(IReadOnlyList<string> assets)
    {
        List<string> symbols = assets.Select(SymbolOf).Where(s => s is not null).Select(s => s!).ToList();
        return symbols.Count == 0 ? [] : [JsonSerializer.Serialize(new { id = "books", type = "subscribe", topic = $"{Topic}:{string.Join(',', symbols)}", privateChannel = false, response = true })];
    }

    public override StreamedBook? Read(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("type", out JsonElement type) || type.GetString() != "message" || !root.TryGetProperty("topic", out JsonElement topic) || !root.TryGetProperty("data", out JsonElement data))
        {
            return null;
        }

        string name = topic.GetString()!;
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && name.AsSpan(0, colon).SequenceEqual(Topic) ? Read(name[(colon + 1)..], data.GetProperty("bids"), data.GetProperty("asks")) : null;
    }

    protected abstract StreamedBook? Read(string symbol, JsonElement bids, JsonElement asks);
}

/// <summary>KuCoin spot against USDT. Sizes are in units of the asset.</summary>
public sealed class KucoinSpotBookStream : KucoinBookStream
{
    public override InstrumentType Type => InstrumentType.Spot;

    protected override string RestHost => "api.kucoin.com";

    protected override string Topic => "/spotMarket/level2Depth50";

    protected override string? SymbolOf(string asset) => $"{asset.ToUpperInvariant()}-USDT";

    protected override StreamedBook? Read(string symbol, JsonElement bids, JsonElement asks) =>
        symbol.EndsWith("-USDT", StringComparison.Ordinal) ? new StreamedBook(symbol[..^5], Levels(bids), Levels(asks)) : null;
}

/// <summary>
/// KuCoin USDT-margined perpetuals. KuCoin calls bitcoin XBT there, and gives sizes in contracts, so the size of
/// each contract is read first and every size turned into units of the asset.
/// </summary>
public sealed class KucoinPerpBookStream : KucoinBookStream
{
    private Dictionary<string, (string Asset, decimal ContractSize)> _contracts = [];
    private Dictionary<string, string> _symbols = [];

    protected override string RestHost => "api-futures.kucoin.com";

    protected override string Topic => "/contractMarket/level2Depth50";

    protected override string? SymbolOf(string asset) => _symbols.GetValueOrDefault(asset.ToUpperInvariant());

    public override bool CanFollow(string asset) => _symbols.ContainsKey(asset.ToUpperInvariant());

    public override async Task PrepareAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync($"https://{RestHost}/api/v1/contracts/active", cancellationToken);
        response.EnsureSuccessStatusCode();
        UseContracts(ReadContracts(await response.Content.ReadAsStringAsync(cancellationToken)));
        await base.PrepareAsync(http, cancellationToken);
    }

    /// <summary>Every open USDT-margined perpetual: its symbol, its asset under the usual name, and the size of one contract.</summary>
    public static Dictionary<string, (string Asset, decimal ContractSize)> ReadContracts(string contracts)
    {
        using JsonDocument document = JsonDocument.Parse(contracts);
        Dictionary<string, (string, decimal)> found = [];
        foreach (JsonElement contract in document.RootElement.GetProperty("data").EnumerateArray())
        {
            string symbol = contract.GetProperty("symbol").GetString()!;
            // Perpetuals have no expiry; "USDTM" is the USDT-margined family.
            if (!symbol.EndsWith("USDTM", StringComparison.Ordinal) || contract.GetProperty("quoteCurrency").GetString() != "USDT" || contract.GetProperty("status").GetString() != "Open" || contract.GetProperty("expireDate").ValueKind != JsonValueKind.Null)
            {
                continue;
            }

            string asset = contract.GetProperty("baseCurrency").GetString()!.ToUpperInvariant();
            decimal size = contract.GetProperty("multiplier").GetDecimal();
            if (size > 0m)
            {
                found[symbol] = (asset == "XBT" ? "BTC" : asset, size);
            }
        }

        return found;
    }

    public void UseContracts(Dictionary<string, (string Asset, decimal ContractSize)> contracts)
    {
        _contracts = contracts;
        _symbols = contracts.GroupBy(c => c.Value.Asset).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Key.Length).First().Key);
    }

    protected override StreamedBook? Read(string symbol, JsonElement bids, JsonElement asks) =>
        _contracts.TryGetValue(symbol, out (string Asset, decimal ContractSize) contract) ? new StreamedBook(contract.Asset, Side(bids, contract.ContractSize), Side(asks, contract.ContractSize)) : null;

    /// <summary>Levels given as [price as text, contracts as a number].</summary>
    private static List<BookLevel> Side(JsonElement side, decimal contractSize)
    {
        List<BookLevel> levels = new(Depth);
        foreach (JsonElement level in side.EnumerateArray())
        {
            decimal quantity = (level[1].ValueKind == JsonValueKind.String ? Number(level[1]) : level[1].GetDecimal()) * contractSize;
            if (quantity > 0m && levels.Count < Depth)
            {
                levels.Add(new BookLevel(level[0].ValueKind == JsonValueKind.String ? Number(level[0]) : level[0].GetDecimal(), quantity));
            }
        }

        return levels;
    }
}

/// <summary>
/// Keeps one venue's stream open and its books in the consolidated book. When the connection drops or goes quiet, the
/// venue's books are removed at once, so nothing is routed on prices that are no longer being confirmed, and the
/// connection is made again, waiting a little longer after each failure.
/// </summary>
public sealed class BookStreamRunner(BookStream stream, ConsolidatedBook books, VenueStatusBoard board, HttpClient http, TimeProvider clock, ILogger logger)
{
    /// <summary>A stream that says nothing for this long is taken to be broken.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);

    /// <summary>How often unchanged books are marked as still good, on venues that only send changes.</summary>
    private static readonly TimeSpan ConfirmEvery = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(30);

    /// <summary>How often the list of wanted books is looked at again.</summary>
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(1);

    /// <summary>A short wait between requests, so that a long list does not arrive as one burst the venue cuts the connection for.</summary>
    private static readonly TimeSpan BetweenRequests = TimeSpan.FromMilliseconds(120);

    /// <summary>How often a stream looks again at what its venue lists, when it is wanted to follow something it does not know.</summary>
    private static readonly TimeSpan LearnAgainAfter = TimeSpan.FromMinutes(2);

    /// <param name="wanted">The assets to follow right now. Asked again every second; books are opened and closed to match.</param>
    public async Task RunAsync(Func<IReadOnlyList<string>> wanted, CancellationToken stopping)
    {
        TimeSpan wait = TimeSpan.FromSeconds(1);
        while (!stopping.IsCancellationRequested)
        {
            DateTimeOffset started = clock.GetUtcNow();
            bool idle = false;
            try
            {
                idle = await ConnectAndReadAsync(wanted, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning("The {Venue} {Type} market data stream stopped: {Reason}", stream.Venue, stream.Type, e.Message);
            }
            finally
            {
                books.Clear(stream.Venue, stream.Type, stream.Quote);
            }

            // A connection that lasted is not a failing one, and neither is having had nothing to follow: start the waits over.
            wait = idle || clock.GetUtcNow() - started > TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(1) : wait;
            try
            {
                await Task.Delay(wait, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            wait = wait * 2 > LongestWait ? LongestWait : wait * 2;
        }
    }

    /// <returns>True when it ended because there was nothing to follow, which is not a failure.</returns>
    private async Task<bool> ConnectAndReadAsync(Func<IReadOnlyList<string>> wanted, CancellationToken stopping)
    {
        if (wanted().Count == 0)
        {
            // Nothing to follow on this venue for now: no connection is held open for it.
            await Task.Delay(LookEvery, stopping);
            return true;
        }

        stream.Reset();
        await stream.PrepareAsync(http, stopping);
        DateTimeOffset learnt = clock.GetUtcNow();
        // Only what the stream can ask for counts as followed; the rest is tried again when it has looked again.
        List<string> assets = wanted().Where(stream.CanFollow).ToList();
        HashSet<string> following = assets.ToHashSet();
        if (assets.Count == 0)
        {
            await Task.Delay(LookEvery, stopping);
            return true;
        }

        using ClientWebSocket socket = new();
        using (CancellationTokenSource connecting = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(stream.Address(assets), connecting.Token);
        }

        // Not under the connecting limit: a long list is asked for at a pace the venue accepts, which can take a few seconds.
        await AskAsync(socket, assets, stream.Subscriptions, stopping);

        logger.LogInformation("The {Venue} {Type} market data stream is open.", stream.Venue, stream.Type);
        Dictionary<string, VenueBook> latest = [];
        byte[] buffer = new byte[64 * 1024];
        using MemoryStream message = new();
        DateTimeOffset pinged = clock.GetUtcNow();
        DateTimeOffset confirmed = pinged;
        DateTimeOffset heard = pinged;
        DateTimeOffset looked = pinged;
        Task<WebSocketReceiveResult>? receiving = null;
        while (!stopping.IsCancellationRequested)
        {
            if (clock.GetUtcNow() - looked >= LookEvery)
            {
                looked = clock.GetUtcNow();
                IReadOnlyList<string> all = wanted();
                if (all.Count == 0)
                {
                    // Nothing left to follow: the connection is given up, and opened again when something is wanted.
                    return true;
                }

                // Wanted, but the venue listed it after this stream last looked: look again now and then, so the
                // book opens without waiting for the connection to drop.
                if (all.Any(a => !stream.CanFollow(a)) && looked - learnt >= LearnAgainAfter)
                {
                    learnt = looked;
                    try
                    {
                        await stream.PrepareAsync(http, stopping);
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException && !stopping.IsCancellationRequested)
                    {
                        // What it knew stays in use; the next look tries again.
                    }
                }

                HashSet<string> next = all.Where(stream.CanFollow).ToHashSet();
                List<string> added = next.Except(following).ToList();
                List<string> dropped = following.Except(next).ToList();
                await AskAsync(socket, added, stream.Subscriptions, stopping);
                await AskAsync(socket, dropped, stream.Unsubscriptions, stopping);

                foreach (string asset in dropped)
                {
                    latest.Remove(asset);
                    books.Remove(stream.Venue, stream.Type, asset, stream.Quote);
                }

                following = next;
            }

            // Wait for the next piece, but not past the next ping: a receive must never be cancelled (that would
            // break the connection), so the same receive is picked up again after the ping has gone out.
            receiving ??= socket.ReceiveAsync(new ArraySegment<byte>(buffer), stopping);
            if (await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(2), stopping)) != receiving)
            {
                DateTimeOffset quiet = clock.GetUtcNow();
                if (quiet - heard > Silence)
                {
                    throw new TimeoutException($"nothing was received for {Silence.TotalSeconds:0} seconds");
                }

                if (stream.Ping is { } keepAlive && quiet - pinged >= PingEvery)
                {
                    pinged = quiet;
                    await socket.SendAsync(Encoding.UTF8.GetBytes(keepAlive), WebSocketMessageType.Text, true, stopping);
                }

                continue;
            }

            WebSocketReceiveResult part = await receiving;
            receiving = null;
            message.Write(buffer, 0, part.Count);
            if (!part.EndOfMessage)
            {
                continue;
            }

            if (part.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException($"the venue closed the connection ({socket.CloseStatusDescription ?? socket.CloseStatus?.ToString() ?? "no reason given"})");
            }

            DateTimeOffset now = heard = clock.GetUtcNow();
            board.Heard(stream.Venue);
            if (part.MessageType == WebSocketMessageType.Text)
            {
                StreamedBook? read;
                try
                {
                    read = stream.Read(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
                catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or ArgumentException or OverflowException)
                {
                    // One message that cannot be read is skipped; the next whole book replaces whatever it carried.
                    logger.LogWarning("A {Venue} market data message could not be read: {Reason}", stream.Venue, e.Message);
                    read = null;
                }

                // A venue may go on sending a book for a moment after it was told to stop; such a book is not kept.
                if (read is not null && following.Contains(read.Asset))
                {
                    VenueBook book = new(stream.Venue, read.Asset, stream.Type, read.Bids, read.Asks, now, stream.Quote);
                    latest[read.Asset] = book;
                    books.Update(book);
                }
            }

            if (stream.SilenceMeansUnchanged && now - confirmed >= ConfirmEvery)
            {
                confirmed = now;
                foreach (VenueBook book in latest.Values.ToList())
                {
                    if (book.At != now)
                    {
                        books.Update(latest[book.Asset] = book with { At = now });
                    }
                }
            }

            if (stream.Ping is { } ping && now - pinged >= PingEvery)
            {
                pinged = now;
                await socket.SendAsync(Encoding.UTF8.GetBytes(ping), WebSocketMessageType.Text, true, stopping);
            }

            message.SetLength(0);
        }

        return false;
    }

    /// <summary>Sends the requests for a list of assets, so many to a message and with a short wait between messages.</summary>
    private async Task AskAsync(ClientWebSocket socket, IReadOnlyList<string> assets, Func<IReadOnlyList<string>, IEnumerable<string>> requests, CancellationToken stopping)
    {
        bool first = true;
        foreach (string[] together in assets.Chunk(Math.Max(1, stream.AskTogether)))
        {
            foreach (string request in requests(together))
            {
                if (!first)
                {
                    await Task.Delay(BetweenRequests, stopping);
                }

                first = false;
                await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, stopping);
            }
        }
    }
}

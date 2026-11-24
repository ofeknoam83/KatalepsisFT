using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Venues.Kucoin;

/// <summary>
/// Real orders at KuCoin: spot against USDT, and USDT-margined perpetuals. Written against KuCoin's own
/// description of its interface (spot "HF" orders on api.kucoin.com, futures orders on api-futures.kucoin.com),
/// with plain requests rather than a library, so that exactly what is sent and how each answer is read is here.
///
/// Every answer falls into one of three: done; refused, when KuCoin itself said no and nothing happened; or
/// unclear, when there was no answer that can be relied on. The caller treats an unclear answer to an order as
/// "it may have arrived".
/// </summary>
public sealed class KucoinOrderGateway(HttpClient http, TimeProvider clock) : IOrderGateway
{
    public const string HttpClientName = "kucoin-orders";
    public const string SpotHost = "https://api.kucoin.com";
    public const string FuturesHost = "https://api-futures.kucoin.com";

    /// <summary>An order that reaches KuCoin later than this after it was sent is refused by KuCoin itself (spot).</summary>
    private const int LatestArrivalMs = 5000;
    private static readonly TimeSpan RulesLife = TimeSpan.FromMinutes(10);
    private static readonly string[] Dollars = ["USDT", "USDC", "USD"];
    private static readonly string[] Unknown = ["not exist", "not found", "no such order", "does not exist"];

    /// <summary>KuCoin's codes for "this request is not allowed": bad headers, key, passphrase, signature, address, permission.</summary>
    private static readonly string[] AboutTheCaller = ["400001", "400002", "400003", "400004", "400005", "400006", "400007"];

    private readonly ConcurrentDictionary<string, (VenueInstrument? Rules, DateTimeOffset At)> _spot = new();
    private (Dictionary<string, PerpContract> ByAsset, DateTimeOffset At)? _perps;
    private readonly ConcurrentDictionary<string, string> _keyVersion = new();
    private readonly ConcurrentDictionary<string, string> _spotAccountType = new();
    private readonly ConcurrentDictionary<(string Key, string Symbol), int> _crossLeverage = new();

    public Venue Venue => Venue.Kucoin;

    private sealed record PerpContract(string Symbol, string Asset, decimal Multiplier, decimal LotSize, decimal TickSize);

    // ---------------------------------------------------------------- rules

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        asset = asset.ToUpperInvariant();
        DateTimeOffset now = clock.GetUtcNow();
        if (type == InstrumentType.Perp)
        {
            PerpContract? contract = (await PerpsAsync(cancellationToken)).GetValueOrDefault(asset);
            // One contract is the step; an order is a whole number of them.
            return contract is null ? null : new VenueInstrument(contract.Symbol, contract.Multiplier * contract.LotSize, contract.TickSize, contract.Multiplier * contract.LotSize, 0m);
        }

        if (type != InstrumentType.Spot)
        {
            return null;
        }

        if (_spot.TryGetValue(asset, out (VenueInstrument? Rules, DateTimeOffset At) known) && now - known.At < RulesLife)
        {
            return known.Rules;
        }

        VenueInstrument? rules;
        try
        {
            JsonElement data = await SendAsync(null, SpotHost, HttpMethod.Get, $"/api/v2/symbols/{asset}-USDT", null, cancellationToken);
            rules = data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("enableTrading", out JsonElement trading) || !trading.GetBoolean()
                ? null
                : new VenueInstrument(data.GetProperty("symbol").GetString()!, Number(data.GetProperty("baseIncrement")), Number(data.GetProperty("priceIncrement")), Number(data.GetProperty("baseMinSize")), data.TryGetProperty("minFunds", out JsonElement funds) && funds.ValueKind != JsonValueKind.Null ? Number(funds) : 0m);
        }
        catch (VenueRefusedException)
        {
            // KuCoin answered that there is no such pair.
            rules = null;
        }

        _spot[asset] = (rules, now);
        return rules;
    }

    private async Task<Dictionary<string, PerpContract>> PerpsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (_perps is { } cached && now - cached.At < RulesLife)
        {
            return cached.ByAsset;
        }

        JsonElement data = await SendAsync(null, FuturesHost, HttpMethod.Get, "/api/v1/contracts/active", null, cancellationToken);
        Dictionary<string, PerpContract> found = [];
        foreach (JsonElement contract in data.EnumerateArray())
        {
            string symbol = contract.GetProperty("symbol").GetString()!;
            if (!symbol.EndsWith("USDTM", StringComparison.Ordinal) || Text(contract, "quoteCurrency") != "USDT" || Text(contract, "status") != "Open" || contract.GetProperty("expireDate").ValueKind != JsonValueKind.Null)
            {
                continue;
            }

            string asset = Text(contract, "baseCurrency")!.ToUpperInvariant();
            asset = asset == "XBT" ? "BTC" : asset;
            decimal multiplier = Number(contract.GetProperty("multiplier"));
            if (multiplier > 0m && (!found.TryGetValue(asset, out PerpContract? other) || symbol.Length < other.Symbol.Length))
            {
                found[asset] = new PerpContract(symbol, asset, multiplier, Math.Max(1m, Number(contract.GetProperty("lotSize"))), Number(contract.GetProperty("tickSize")));
            }
        }

        _perps = (found, now);
        return found;
    }

    // ---------------------------------------------------------------- orders

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        bool postOnly = order.TimeInForce == TimeInForce.PostOnly;
        string timeInForce = order.TimeInForce == TimeInForce.ImmediateOrCancel ? "IOC" : "GTC";
        string side = order.Side == OrderSide.Buy ? "buy" : "sell";
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        if (order.Type == InstrumentType.Spot)
        {
            Dictionary<string, object> body = new()
            {
                ["clientOid"] = order.ClientId,
                ["symbol"] = instrument.Symbol,
                ["side"] = side,
                ["type"] = "limit",
                ["price"] = Plain(price),
                ["size"] = Plain(order.Quantity),
                ["timeInForce"] = timeInForce,
                // Arriving late is worse than not arriving: KuCoin drops the order if it took longer than this to get there.
                ["allowMaxTimeWindow"] = LatestArrivalMs,
                ["clientTimestamp"] = clock.GetUtcNow().ToUnixTimeMilliseconds(),
            };
            if (postOnly)
            {
                body["postOnly"] = true;
            }

            return Text(await SendAsync(credentials, SpotHost, HttpMethod.Post, "/api/v1/hf/orders", body, cancellationToken), "orderId");
        }

        decimal lots = order.Quantity / instrument.QuantityStep;
        if (lots != Math.Floor(lots) || lots < 1m)
        {
            throw new VenueRefusedException($"The quantity is not a whole number of contracts of {Plain(instrument.QuantityStep)}.");
        }

        int leverage = (int)Math.Clamp(Math.Floor(order.Leverage), 1m, 100m);
        bool cross = order.MarginMode == MarginMode.Cross;
        if (cross && !order.ReduceOnly && (!_crossLeverage.TryGetValue((credentials.ApiKey, instrument.Symbol), out int set) || set != leverage))
        {
            // Leverage is ours to set, never whatever the account was left at. Nothing has been sent yet if this is refused.
            await SendAsync(credentials, FuturesHost, HttpMethod.Post, "/api/v2/changeCrossUserLeverage", new Dictionary<string, object> { ["symbol"] = instrument.Symbol, ["leverage"] = leverage.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
            _crossLeverage[(credentials.ApiKey, instrument.Symbol)] = leverage;
        }

        Dictionary<string, object> futures = new()
        {
            ["clientOid"] = order.ClientId,
            ["symbol"] = instrument.Symbol,
            ["side"] = side,
            ["type"] = "limit",
            ["price"] = Plain(price),
            ["size"] = (long)lots,
            ["timeInForce"] = timeInForce,
            ["reduceOnly"] = order.ReduceOnly,
            ["marginMode"] = cross ? "CROSS" : "ISOLATED",
        };
        if (!cross)
        {
            futures["leverage"] = leverage;
        }

        if (postOnly)
        {
            futures["postOnly"] = true;
        }

        return Text(await SendAsync(credentials, FuturesHost, HttpMethod.Post, "/api/v1/orders", futures, cancellationToken), "orderId");
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        string clientId = order.ClientId;
        string path = type == InstrumentType.Spot ? "/api/v1/hf/orders/client-order/" : "/api/v1/orders/client-order/";
        await SendAsync(credentials, type == InstrumentType.Spot ? SpotHost : FuturesHost, HttpMethod.Delete, $"{path}{Uri.EscapeDataString(clientId)}?symbol={instrument.Symbol}", null, cancellationToken);
    }

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        string clientId = order.ClientId;
        JsonElement data;
        try
        {
            data = type == InstrumentType.Spot
                ? await SendAsync(credentials, SpotHost, HttpMethod.Get, $"/api/v1/hf/orders/client-order/{Uri.EscapeDataString(clientId)}?symbol={instrument.Symbol}", null, cancellationToken)
                : await SendAsync(credentials, FuturesHost, HttpMethod.Get, $"/api/v1/orders/byClientOid?clientOid={Uri.EscapeDataString(clientId)}", null, cancellationToken);
        }
        catch (VenueRefusedException e) when (IsOrderUnknown(e.Message))
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        string? id = Text(data, "id");
        if (type == InstrumentType.Spot)
        {
            bool active = data.TryGetProperty("active", out JsonElement a) && a.ValueKind == JsonValueKind.True;
            decimal? fee = data.TryGetProperty("fee", out JsonElement f) && f.ValueKind != JsonValueKind.Null && Dollars.Contains(Text(data, "feeCurrency") ?? string.Empty, StringComparer.OrdinalIgnoreCase) ? Number(f) : null;
            return new VenueOrderReport(active ? VenueOrderState.Open : VenueOrderState.Done, NumberOr(data, "dealSize"), NumberOr(data, "dealFunds"), fee, id);
        }

        bool open = Text(data, "status") == "open" || (data.TryGetProperty("isActive", out JsonElement isActive) && isActive.ValueKind == JsonValueKind.True);
        decimal filled = NumberOr(data, "filledSize") * instrument.QuantityStep;
        decimal? fees = null;
        if (!open && filled > 0m && id is not null)
        {
            fees = await FuturesFeesAsync(credentials, id, cancellationToken);
        }

        return new VenueOrderReport(open ? VenueOrderState.Open : VenueOrderState.Done, filled, NumberOr(data, "filledValue"), fees, id);
    }

    /// <summary>What a finished futures order paid in fees, from its fills; null while KuCoin does not list them yet.</summary>
    private async Task<decimal?> FuturesFeesAsync(VenueCredentials credentials, string orderId, CancellationToken cancellationToken)
    {
        try
        {
            JsonElement data = await SendAsync(credentials, FuturesHost, HttpMethod.Get, $"/api/v1/fills?orderId={Uri.EscapeDataString(orderId)}&pageSize=1000", null, cancellationToken);
            if (!data.TryGetProperty("items", out JsonElement items) || items.GetArrayLength() == 0)
            {
                return null;
            }

            decimal total = 0m;
            foreach (JsonElement fill in items.EnumerateArray())
            {
                if (!Dollars.Contains(Text(fill, "feeCurrency") ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    return null;
                }

                total += NumberOr(fill, "fee");
            }

            return total;
        }
        catch (Exception e) when (e is VenueRefusedException or VenueUnclearException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a refusal says "there is no such order". It must be about the order: KuCoin also says "not exists" of
    /// a key it does not know, and reading that as "the order is not there" would write off an order that is.
    /// </summary>
    public static bool IsOrderUnknown(string refusal) =>
        refusal.Contains("order", StringComparison.OrdinalIgnoreCase)
        && Unknown.Any(u => refusal.Contains(u, StringComparison.OrdinalIgnoreCase))
        && !refusal.Contains("KC-API", StringComparison.OrdinalIgnoreCase)
        && !AboutTheCaller.Any(code => refusal.Contains($"(code {code})", StringComparison.Ordinal));

    // ---------------------------------------------------------------- account

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        if (!_spotAccountType.TryGetValue(credentials.ApiKey, out string? accountType))
        {
            // Accounts that used KuCoin's high-frequency trading before 2024 keep their trading money in "trade_hf".
            JsonElement opened = await SendAsync(credentials, SpotHost, HttpMethod.Get, "/api/v1/hf/accounts/opened", null, cancellationToken);
            _spotAccountType[credentials.ApiKey] = accountType = opened.ValueKind == JsonValueKind.True ? "trade_hf" : "trade";
        }

        JsonElement accounts = await SendAsync(credentials, SpotHost, HttpMethod.Get, $"/api/v1/accounts?type={accountType}", null, cancellationToken);
        JsonElement prices = await SendAsync(null, SpotHost, HttpMethod.Get, "/api/v1/prices?base=USD", null, cancellationToken);
        decimal cash = 0m;
        List<VenueHolding> holdings = [];
        foreach (JsonElement account in accounts.EnumerateArray())
        {
            string currency = Text(account, "currency")!.ToUpperInvariant();
            decimal balance = NumberOr(account, "balance");
            if (balance <= 0m)
            {
                continue;
            }

            if (Dollars.Contains(currency))
            {
                cash += balance;
            }
            else
            {
                holdings.Add(new VenueHolding(currency, InstrumentType.Spot, balance, null, null, prices.TryGetProperty(currency, out JsonElement price) ? Number(price) : null));
            }
        }

        decimal? futuresEquity = null;
        bool perpsRead = true;
        try
        {
            futuresEquity = NumberOr(await SendAsync(credentials, FuturesHost, HttpMethod.Get, "/api/v1/account-overview?currency=USDT", null, cancellationToken), "accountEquity");
            Dictionary<string, PerpContract> bySymbol = (await PerpsAsync(cancellationToken)).Values.ToDictionary(c => c.Symbol);
            foreach (JsonElement position in (await SendAsync(credentials, FuturesHost, HttpMethod.Get, "/api/v1/positions", null, cancellationToken)).EnumerateArray())
            {
                decimal lots = NumberOr(position, "currentQty");
                if (lots != 0m && bySymbol.TryGetValue(Text(position, "symbol") ?? string.Empty, out PerpContract? contract))
                {
                    decimal liquidation = NumberOr(position, "liquidationPrice");
                    holdings.Add(new VenueHolding(contract.Asset, InstrumentType.Perp, lots * contract.Multiplier, NumberOr(position, "avgEntryPrice"), liquidation > 0m ? liquidation : null, NumberOr(position, "markPrice")));
                }
            }
        }
        catch (VenueRefusedException)
        {
            // The key may not use futures, or the account has none: the spot side stands on its own. But a refusal can
            // also be a passing one (a late timestamp, a limit), so the report says the futures side was not read, and
            // perpetuals already on record are then left as they are rather than taken to be gone.
            perpsRead = false;
        }

        return new VenueAccountReport(cash, futuresEquity, holdings, perpsRead);
    }

    // ---------------------------------------------------------------- one request

    private async Task<JsonElement> SendAsync(VenueCredentials? credentials, string host, HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
    {
        string json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        string version = credentials is null ? string.Empty : _keyVersion.GetValueOrDefault(credentials.ApiKey, "3");
        (HttpStatusCode status, string code, string message, JsonElement data) = await OnceAsync(credentials, version, host, method, pathAndQuery, json, cancellationToken);
        // Keys made before KuCoin's third key format are refused when announced as the third. Refused for who
        // is asking, nothing was done: asking again as the second format is safe.
        if (credentials is not null && version == "3" && !_keyVersion.ContainsKey(credentials.ApiKey) && code is "400003" or "400004" or "400005")
        {
            (HttpStatusCode Status, string Code, string Message, JsonElement Data) second = await OnceAsync(credentials, "2", host, method, pathAndQuery, json, cancellationToken);
            if (second.Code == "200000")
            {
                _keyVersion[credentials.ApiKey] = "2";
                return second.Data;
            }

            // If the second attempt was refused for the same reason, the first refusal stands. Anything else it said
            // is the answer that counts: a broken or busy reply to it means the request may have been carried out.
            if (second.Code is not ("400003" or "400004" or "400005"))
            {
                (status, code, message, data) = second;
            }
        }

        if (code == "200000")
        {
            if (credentials is not null)
            {
                _keyVersion.TryAdd(credentials.ApiKey, version);
            }

            return data;
        }

        string reason = $"{(string.IsNullOrWhiteSpace(message) ? "no reason given" : message)} (code {(code.Length == 0 ? ((int)status).ToString(CultureInfo.InvariantCulture) : code)})";
        // Busy, rate-limited or broken: not an answer about the request itself.
        if ((int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || code is "429000" or "230005" or "200002" or "500000" || code.Length == 0)
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }

    private async Task<(HttpStatusCode Status, string Code, string Message, JsonElement Data)> OnceAsync(VenueCredentials? credentials, string version, string host, HttpMethod method, string pathAndQuery, string json, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, host + pathAndQuery);
        if (json.Length > 0)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (credentials is not null)
        {
            string timestamp = clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            byte[] secret = Encoding.UTF8.GetBytes(credentials.ApiSecret);
            request.Headers.Add("KC-API-KEY", credentials.ApiKey);
            request.Headers.Add("KC-API-SIGN", Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(timestamp + method.Method.ToUpperInvariant() + pathAndQuery + json))));
            request.Headers.Add("KC-API-TIMESTAMP", timestamp);
            request.Headers.Add("KC-API-PASSPHRASE", Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(credentials.Passphrase ?? string.Empty))));
            request.Headers.Add("KC-API-KEY-VERSION", version);
        }

        HttpResponseMessage response;
        string text;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new VenueUnclearException(e is OperationCanceledException ? "no answer in time" : "the connection failed", e);
        }

        using (response)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                string code = root.TryGetProperty("code", out JsonElement c) ? c.ValueKind == JsonValueKind.String ? c.GetString()! : c.GetRawText() : string.Empty;
                string message = root.TryGetProperty("msg", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : string.Empty;
                return (response.StatusCode, code, message, root.TryGetProperty("data", out JsonElement d) ? d.Clone() : default);
            }
            catch (JsonException)
            {
                return (response.StatusCode, string.Empty, "an answer that could not be read", default);
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture) : value.GetDecimal();

    private static decimal NumberOr(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number && (value.ValueKind == JsonValueKind.Number || value.GetString()!.Length > 0) ? Number(value) : 0m;

    /// <summary>A number as KuCoin wants it: plain digits, no exponent, no trailing zeros.</summary>
    private static string Plain(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}

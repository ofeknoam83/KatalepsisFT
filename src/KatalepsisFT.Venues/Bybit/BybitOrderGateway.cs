using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Orders;

namespace KatalepsisFT.Venues.Bybit;

/// <summary>
/// Real orders at Bybit (version 5, unified trading account): spot against USDT and USDT perpetuals. Both use the
/// same symbol; the "category" says which is meant. Quantities are in units of the asset on both. Bybit takes an
/// order first and decides on it afterwards, so "taken" is never read as "in the market": the order is asked about.
/// The margin mode there belongs to the whole account and is left as the account has it.
/// </summary>
public sealed class BybitOrderGateway(HttpClient http, TimeProvider clock) : RestOrderGateway(http, clock), IOrderGateway
{
    public const string HttpClientName = "bybit-orders";
    public const string Host = "https://api.bybit.com";
    private const string Window = "5000";

    /// <summary>Busy, timed out or limited.</summary>
    private static readonly string[] NotAnswers = ["10000", "10006", "10016", "10018", "110079", "170001", "170007", "170146", "170147"];
    private static readonly string[] Final = ["Filled", "Cancelled", "PartiallyFilledCanceled", "Rejected", "Deactivated"];
    private readonly ConcurrentDictionary<(string Key, string Symbol), int> _leverage = new();

    public Venue Venue => Venue.Bybit;

    private static string Category(InstrumentType type) => type == InstrumentType.Spot ? "spot" : "linear";

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        if (type is not (InstrumentType.Spot or InstrumentType.Perp))
        {
            return null;
        }

        string symbol = asset.ToUpperInvariant() + "USDT";
        return await KeptAsync<VenueInstrument?>($"{type}:{symbol}", async () =>
        {
            JsonElement result;
            try
            {
                result = await SendAsync(null, HttpMethod.Get, "/v5/market/instruments-info", $"category={Category(type)}&symbol={symbol}", null, cancellationToken);
            }
            catch (VenueRefusedException)
            {
                return null;
            }

            if (!result.TryGetProperty("list", out JsonElement list) || list.GetArrayLength() == 0)
            {
                return null;
            }

            // Suspended or being delisted: still listed, so its orders can be asked about and cancelled.
            bool trading = Text(list[0], "status") == "Trading";

            JsonElement lot = list[0].GetProperty("lotSizeFilter");
            decimal tick = Number(list[0].GetProperty("priceFilter"), "tickSize");
            // Spot calls its quantity step "basePrecision" and its smallest order by value "minOrderAmt".
            decimal step = type == InstrumentType.Spot ? Number(lot, "basePrecision") : Number(lot, "qtyStep");
            decimal notional = type == InstrumentType.Spot ? Number(lot, "minOrderAmt") : Number(lot, "minNotionalValue");
            return step > 0m && tick > 0m ? new VenueInstrument(symbol, step, tick, Number(lot, "minOrderQty"), notional, trading) : null;
        });
    }

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        bool spot = order.Type == InstrumentType.Spot;
        Dictionary<string, object> body = new()
        {
            ["category"] = Category(order.Type),
            ["symbol"] = instrument.Symbol,
            ["side"] = order.Side == OrderSide.Buy ? "Buy" : "Sell",
            ["orderType"] = "Limit",
            ["qty"] = Plain(order.Quantity),
            ["price"] = Plain(price),
            ["timeInForce"] = order.TimeInForce switch
            {
                TimeInForce.ImmediateOrCancel => "IOC",
                TimeInForce.PostOnly => "PostOnly",
                _ => "GTC",
            },
            ["orderLinkId"] = order.ClientId,
        };
        if (!spot)
        {
            // One-way mode. In hedge mode Bybit refuses this and says so.
            body["positionIdx"] = 0;
            if (order.ReduceOnly)
            {
                body["reduceOnly"] = true;
            }
            else
            {
                await SetLeverageAsync(credentials, instrument.Symbol, order.Leverage, cancellationToken);
            }
        }

        return Text(await SendAsync(credentials, HttpMethod.Post, "/v5/order/create", string.Empty, body, cancellationToken), "orderId");
    }

    private async Task SetLeverageAsync(VenueCredentials credentials, string symbol, decimal wanted, CancellationToken cancellationToken)
    {
        int leverage = (int)Math.Clamp(Math.Floor(wanted), 1m, 100m);
        if (_leverage.TryGetValue((credentials.ApiKey, symbol), out int set) && set == leverage)
        {
            return;
        }

        string text = leverage.ToString(CultureInfo.InvariantCulture);
        try
        {
            await SendAsync(credentials, HttpMethod.Post, "/v5/position/set-leverage", string.Empty, new Dictionary<string, object> { ["category"] = "linear", ["symbol"] = symbol, ["buyLeverage"] = text, ["sellLeverage"] = text }, cancellationToken);
        }
        catch (VenueRefusedException e) when (e.Message.Contains("(code 110043)", StringComparison.Ordinal))
        {
            // "Set leverage has not been modified": it is already what we want.
        }

        _leverage[(credentials.ApiKey, symbol)] = leverage;
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken) =>
        await SendAsync(credentials, HttpMethod.Post, "/v5/order/cancel", string.Empty, new Dictionary<string, object> { ["category"] = Category(type), ["symbol"] = instrument.Symbol, ["orderLinkId"] = order.ClientId }, cancellationToken);

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        string query = $"category={Category(type)}&symbol={instrument.Symbol}&orderLinkId={order.ClientId}";
        // Open orders and the latest finished ones first; that list is emptied when Bybit restarts, so the history is asked next.
        JsonElement? found = First(await SendAsync(credentials, HttpMethod.Get, "/v5/order/realtime", query, null, cancellationToken))
            ?? First(await SendAsync(credentials, HttpMethod.Get, "/v5/order/history", query, null, cancellationToken));
        if (found is not { } o)
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        decimal filled = Number(o, "cumExecQty");
        decimal value = Number(o, "cumExecValue");
        decimal? fees = null;
        // An empty list of fees means "not known yet", not "nothing was charged".
        if (o.TryGetProperty("cumFeeDetail", out JsonElement detail) && detail.ValueKind == JsonValueKind.Object && detail.EnumerateObject().Any())
        {
            fees = FeesInDollars(detail.EnumerateObject().Select(f => (Number(f.Value), (string?)f.Name)), instrument.Symbol[..^4], filled > 0m ? value / filled : 0m);
        }

        return new VenueOrderReport(Final.Contains(Text(o, "orderStatus") ?? string.Empty) ? VenueOrderState.Done : VenueOrderState.Open, filled, value, filled > 0m ? fees : null, Text(o, "orderId"), Text(o, "rejectReason") is { Length: > 0 } why && why != "EC_NoError" ? why : null);
    }

    private static JsonElement? First(JsonElement result) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("list", out JsonElement list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 ? list[0] : null;

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement wallet = await SendAsync(credentials, HttpMethod.Get, "/v5/account/wallet-balance", "accountType=UNIFIED", null, cancellationToken);
        decimal cash = 0m;
        List<VenueHolding> holdings = [];
        if (First(wallet) is { } account && account.TryGetProperty("coin", out JsonElement coins))
        {
            foreach (JsonElement coin in coins.EnumerateArray())
            {
                string name = (Text(coin, "coin") ?? string.Empty).ToUpperInvariant();
                if (IsDollar(name))
                {
                    // One wallet for spot and perpetuals: a dollar coin's equity already includes the perpetuals' open profit and loss.
                    cash += Number(coin, "equity");
                    continue;
                }

                decimal held = Number(coin, "walletBalance");
                if (held > 0m)
                {
                    holdings.Add(new VenueHolding(name, InstrumentType.Spot, held, null, null, Number(coin, "usdValue") is > 0m and decimal usd ? usd / held : null));
                }
            }
        }

        JsonElement positions = await SendAsync(credentials, HttpMethod.Get, "/v5/position/list", "category=linear&settleCoin=USDT&limit=200", null, cancellationToken);
        if (positions.TryGetProperty("list", out JsonElement list))
        {
            foreach (JsonElement position in list.EnumerateArray())
            {
                string symbol = Text(position, "symbol") ?? string.Empty;
                decimal size = Number(position, "size");
                if (size > 0m && symbol.EndsWith("USDT", StringComparison.Ordinal))
                {
                    decimal liquidation = Number(position, "liqPrice");
                    holdings.Add(new VenueHolding(symbol[..^4], InstrumentType.Perp, Text(position, "side") == "Sell" ? -size : size, Number(position, "avgPrice"), liquidation > 0m ? liquidation : null, Number(position, "markPrice")));
                }
            }
        }

        return new VenueAccountReport(cash, null, holdings);
    }

    private async Task<JsonElement> SendAsync(VenueCredentials? credentials, HttpMethod method, string path, string query, object? body, CancellationToken cancellationToken)
    {
        string json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        using HttpRequestMessage request = new(method, Host + path + (query.Length > 0 ? "?" + query : string.Empty));
        if (json.Length > 0)
        {
            request.Content = Body(json);
        }

        if (credentials is not null)
        {
            string timestamp = NowMs.ToString(CultureInfo.InvariantCulture);
            request.Headers.Add("X-BAPI-API-KEY", credentials.ApiKey);
            request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
            request.Headers.Add("X-BAPI-RECV-WINDOW", Window);
            request.Headers.Add("X-BAPI-SIGN", HmacHex(credentials.ApiSecret, timestamp + credentials.ApiKey + Window + (json.Length > 0 ? json : query)));
        }

        (HttpStatusCode status, string text) = await ExchangeAsync(request, cancellationToken);
        JsonElement? answer = Json(text);
        string code = answer is { ValueKind: JsonValueKind.Object } read ? Text(read, "retCode") ?? string.Empty : string.Empty;
        if (code == "0" && answer is { } good)
        {
            return good.TryGetProperty("result", out JsonElement result) ? result : default;
        }

        string reason = $"{Limited(answer is { ValueKind: JsonValueKind.Object } said ? Text(said, "retMsg") : null)} (code {(code.Length == 0 ? ((int)status).ToString(CultureInfo.InvariantCulture) : code)})";
        // Bybit also answers 403 when it is limiting an address; that says nothing about the request.
        if (NotAnAnswer(status) || status == HttpStatusCode.Forbidden || code.Length == 0 || NotAnswers.Contains(code))
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }
}

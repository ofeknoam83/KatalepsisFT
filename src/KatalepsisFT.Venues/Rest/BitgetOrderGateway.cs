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

namespace KatalepsisFT.Venues.Rest;

/// <summary>
/// Real orders at Bitget (version 2, the "classic" account): spot against USDT and USDT-margined perpetuals.
/// Quantities are in units of the asset on both. The futures account must be in one-way mode. An account that
/// Bitget has moved to its newer unified account is not served by this interface.
/// </summary>
public sealed class BitgetOrderGateway(HttpClient http, TimeProvider clock) : RestOrderGateway(http, clock), IOrderGateway
{
    public const string HttpClientName = "bitget-orders";
    public const string Host = "https://api.bitget.com";
    private const string Family = "USDT-FUTURES";

    /// <summary>Busy, timed out, being upgraded or limited.</summary>
    private static readonly string[] NotAnswers = ["429", "40010", "40015", "40200", "40725", "40808", "45001", "50031", "80002"];
    private static readonly string[] OrderUnknown = ["40109", "43001", "45057", "31007"];
    private readonly ConcurrentDictionary<(string Key, string Symbol), (int Leverage, MarginMode Mode)> _set = new();

    public Venue Venue => Venue.Bitget;

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        if (type is not (InstrumentType.Spot or InstrumentType.Perp))
        {
            return null;
        }

        string symbol = asset.ToUpperInvariant() + "USDT";
        return await KeptAsync<VenueInstrument?>($"{type}:{symbol}", async () =>
        {
            JsonElement data;
            try
            {
                data = type == InstrumentType.Spot
                    ? await SendAsync(null, HttpMethod.Get, $"/api/v2/spot/public/symbols?symbol={symbol}", null, cancellationToken)
                    : await SendAsync(null, HttpMethod.Get, $"/api/v2/mix/market/contracts?productType={Family}&symbol={symbol}", null, cancellationToken);
            }
            catch (VenueRefusedException)
            {
                return null;
            }

            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement listed = data[0];
            if (type == InstrumentType.Spot)
            {
                // Spot gives numbers of decimals, not steps.
                return Text(listed, "status") != "online" ? null : new VenueInstrument(symbol, Step(Number(listed, "quantityPrecision")), Step(Number(listed, "pricePrecision")), 0m, Number(listed, "minTradeUSDT"));
            }

            decimal step = Number(listed, "sizeMultiplier");
            decimal tick = Math.Max(1m, Number(listed, "priceEndStep")) * Step(Number(listed, "pricePlace"));
            return Text(listed, "symbolStatus") != "normal" || step <= 0m ? null : new VenueInstrument(symbol, step, tick, Number(listed, "minTradeNum"), Number(listed, "minTradeUSDT"));
        });
    }

    /// <summary>The step that goes with a number of decimal places: 2 gives 0.01.</summary>
    private static decimal Step(decimal places)
    {
        decimal step = 1m;
        for (int i = 0; i < (int)Math.Clamp(places, 0m, 18m); i++)
        {
            step /= 10m;
        }

        return step;
    }

    private static string Force(TimeInForce timeInForce) =>
        timeInForce switch
        {
            TimeInForce.ImmediateOrCancel => "ioc",
            TimeInForce.PostOnly => "post_only",
            _ => "gtc",
        };

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        Dictionary<string, object> body = new()
        {
            ["symbol"] = instrument.Symbol,
            ["side"] = order.Side == OrderSide.Buy ? "buy" : "sell",
            ["orderType"] = "limit",
            ["force"] = Force(order.TimeInForce),
            ["price"] = Plain(price),
            ["size"] = Plain(order.Quantity),
            ["clientOid"] = order.ClientId,
        };
        if (order.Type == InstrumentType.Spot)
        {
            return Text(await SendAsync(credentials, HttpMethod.Post, "/api/v2/spot/trade/place-order", body, cancellationToken), "orderId");
        }

        body["productType"] = Family;
        body["marginCoin"] = "USDT";
        body["marginMode"] = order.MarginMode == MarginMode.Cross ? "crossed" : "isolated";
        if (order.ReduceOnly)
        {
            body["reduceOnly"] = "YES";
        }
        else
        {
            int leverage = (int)Math.Clamp(Math.Floor(order.Leverage), 1m, 125m);
            if (!_set.TryGetValue((credentials.ApiKey, instrument.Symbol), out (int Leverage, MarginMode Mode) set) || set != (leverage, order.MarginMode))
            {
                // Leverage is ours to set, never whatever the account was left at. Nothing has been sent if this is refused.
                await SendAsync(credentials, HttpMethod.Post, "/api/v2/mix/account/set-leverage", new Dictionary<string, object> { ["symbol"] = instrument.Symbol, ["productType"] = Family, ["marginCoin"] = "USDT", ["leverage"] = leverage.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
                _set[(credentials.ApiKey, instrument.Symbol)] = (leverage, order.MarginMode);
            }
        }

        return Text(await SendAsync(credentials, HttpMethod.Post, "/api/v2/mix/order/place-order", body, cancellationToken), "orderId");
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        Dictionary<string, object> body = new() { ["symbol"] = instrument.Symbol, ["clientOid"] = order.ClientId };
        if (type != InstrumentType.Spot)
        {
            body["productType"] = Family;
        }

        await SendAsync(credentials, HttpMethod.Post, type == InstrumentType.Spot ? "/api/v2/spot/trade/cancel-order" : "/api/v2/mix/order/cancel-order", body, cancellationToken);
    }

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        bool spot = type == InstrumentType.Spot;
        JsonElement data;
        try
        {
            data = spot
                ? await SendAsync(credentials, HttpMethod.Get, $"/api/v2/spot/trade/orderInfo?clientOid={order.ClientId}", null, cancellationToken)
                : await SendAsync(credentials, HttpMethod.Get, $"/api/v2/mix/order/detail?symbol={instrument.Symbol}&productType={Family}&clientOid={order.ClientId}", null, cancellationToken);
        }
        catch (VenueRefusedException e) when (OrderUnknown.Any(code => e.Message.Contains($"(code {code})", StringComparison.Ordinal)))
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        // Spot answers with a list, futures with the order itself.
        JsonElement? found = data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() > 0 ? data[0] : null : data.ValueKind == JsonValueKind.Object ? data : null;
        if (found is not { } o)
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        // Spot says "status" and "cancelled", futures "state" and "canceled".
        string state = (Text(o, spot ? "status" : "state") ?? string.Empty).ToLowerInvariant();
        bool done = state is "filled" or "cancelled" or "canceled";
        decimal filled = Number(o, "baseVolume");
        decimal value = Number(o, "quoteVolume");
        decimal? fees = filled <= 0m ? null : spot ? SpotFees(o, instrument.Symbol[..^4], value / filled) : Text(o, "fee") is { Length: > 0 } ? Math.Abs(Number(o, "fee")) : null;
        return new VenueOrderReport(done ? VenueOrderState.Done : VenueOrderState.Open, filled, value, fees, Text(o, "orderId"));
    }

    /// <summary>
    /// A spot order's fee. Bitget gives it as text holding JSON, in the coin that was received: the asset on a buy,
    /// USDT on a sell. If part was paid in Bitget's own token there is no figure.
    /// </summary>
    private static decimal? SpotFees(JsonElement order, string asset, decimal price)
    {
        if (Text(order, "feeDetail") is not { Length: > 0 } detail || Json(detail) is not { ValueKind: JsonValueKind.Object } parsed || !parsed.TryGetProperty("newFees", out JsonElement fees))
        {
            return null;
        }

        if (Number(fees, "d") != 0m)
        {
            return null;
        }

        return FeesInDollars([(Number(fees, "t"), Text(order, "side") == "buy" ? asset : "USDT")], asset, price);
    }

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement assets = await SendAsync(credentials, HttpMethod.Get, "/api/v2/spot/account/assets", null, cancellationToken);
        JsonElement tickers = await SendAsync(null, HttpMethod.Get, "/api/v2/spot/market/tickers", null, cancellationToken);
        Dictionary<string, decimal> prices = [];
        foreach (JsonElement ticker in tickers.EnumerateArray())
        {
            string symbol = Text(ticker, "symbol") ?? string.Empty;
            if (symbol.EndsWith("USDT", StringComparison.Ordinal))
            {
                prices[symbol[..^4]] = Number(ticker, "lastPr");
            }
        }

        decimal cash = 0m;
        List<VenueHolding> holdings = [];
        foreach (JsonElement coin in assets.EnumerateArray())
        {
            string name = (Text(coin, "coin") ?? string.Empty).ToUpperInvariant();
            decimal held = Number(coin, "available") + Number(coin, "frozen") + Number(coin, "locked");
            if (held <= 0m)
            {
                continue;
            }

            if (IsDollar(name))
            {
                cash += held;
            }
            else
            {
                holdings.Add(new VenueHolding(name, InstrumentType.Spot, held, null, null, prices.TryGetValue(name, out decimal price) ? price : null));
            }
        }

        decimal? futuresEquity = null;
        bool perpsRead = true;
        try
        {
            JsonElement accounts = await SendAsync(credentials, HttpMethod.Get, $"/api/v2/mix/account/accounts?productType={Family}", null, cancellationToken);
            futuresEquity = accounts.ValueKind == JsonValueKind.Array ? accounts.EnumerateArray().Where(a => IsDollar(Text(a, "marginCoin"))).Sum(a => Number(a, "accountEquity")) : 0m;
            JsonElement positions = await SendAsync(credentials, HttpMethod.Get, $"/api/v2/mix/position/all-position?productType={Family}&marginCoin=USDT", null, cancellationToken);
            if (positions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement position in positions.EnumerateArray())
                {
                    string symbol = (Text(position, "symbol") ?? string.Empty).ToUpperInvariant();
                    decimal size = Number(position, "total");
                    if (size > 0m && symbol.EndsWith("USDT", StringComparison.Ordinal))
                    {
                        decimal liquidation = Number(position, "liquidationPrice");
                        holdings.Add(new VenueHolding(symbol[..^4], InstrumentType.Perp, Text(position, "holdSide") == "short" ? -size : size, Number(position, "openPriceAvg"), liquidation > 0m ? liquidation : null, Number(position, "markPrice")));
                    }
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

    private async Task<JsonElement> SendAsync(VenueCredentials? credentials, HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
    {
        string json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        using HttpRequestMessage request = new(method, Host + pathAndQuery);
        if (json.Length > 0)
        {
            request.Content = Body(json);
        }

        if (credentials is not null)
        {
            string timestamp = NowMs.ToString(CultureInfo.InvariantCulture);
            request.Headers.Add("ACCESS-KEY", credentials.ApiKey);
            request.Headers.Add("ACCESS-SIGN", HmacBase64(credentials.ApiSecret, timestamp + method.Method.ToUpperInvariant() + pathAndQuery + json));
            request.Headers.Add("ACCESS-TIMESTAMP", timestamp);
            request.Headers.Add("ACCESS-PASSPHRASE", credentials.Passphrase ?? string.Empty);
            request.Headers.Add("locale", "en-US");
        }

        (HttpStatusCode status, string text) = await ExchangeAsync(request, cancellationToken);
        JsonElement? answer = Json(text);
        string code = answer is { ValueKind: JsonValueKind.Object } read ? Text(read, "code") ?? string.Empty : string.Empty;
        if (code == "00000" && answer is { } good)
        {
            return good.TryGetProperty("data", out JsonElement data) ? data : default;
        }

        // One of its endpoints says "message" where the others say "msg".
        string? message = answer is { ValueKind: JsonValueKind.Object } said ? Text(said, "msg") ?? Text(said, "message") : null;
        string reason = $"{Limited(message)} (code {(code.Length == 0 ? ((int)status).ToString(CultureInfo.InvariantCulture) : code)})";
        if (NotAnAnswer(status) || code.Length == 0 || NotAnswers.Contains(code))
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }
}

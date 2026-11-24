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

namespace KatalepsisFT.Venues.Binance;

/// <summary>
/// Real orders at Binance: spot against USDT (api.binance.com) and USDT-margined perpetuals (fapi.binance.com), with
/// one key for both. Quantities are in units of the asset on both. The futures account must be in one-way mode;
/// in hedge mode Binance refuses these orders and says so.
/// </summary>
public sealed class BinanceOrderGateway(HttpClient http, TimeProvider clock) : RestOrderGateway(http, clock), IOrderGateway
{
    public const string HttpClientName = "binance-orders";
    public const string SpotHost = "https://api.binance.com";
    public const string FuturesHost = "https://fapi.binance.com";

    /// <summary>Busy, timed out or limited: Binance's codes that do not say whether the request was carried out.</summary>
    private static readonly string[] NotAnswers = ["-1000", "-1001", "-1003", "-1006", "-1007", "-1008", "-1015", "-1016"];
    private static readonly string[] Final = ["FILLED", "CANCELED", "REJECTED", "EXPIRED", "EXPIRED_IN_MATCH"];
    private readonly ConcurrentDictionary<(string Key, string Symbol), (int Leverage, MarginMode Mode)> _set = new();

    public Venue Venue => Venue.Binance;

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        string symbol = asset.ToUpperInvariant() + "USDT";
        if (type == InstrumentType.Spot)
        {
            return await KeptAsync<VenueInstrument?>("spot:" + symbol, async () =>
            {
                try
                {
                    JsonElement info = await SendAsync(null, SpotHost, HttpMethod.Get, "/api/v3/exchangeInfo", $"symbol={symbol}", cancellationToken);
                    return info.TryGetProperty("symbols", out JsonElement symbols) && symbols.GetArrayLength() > 0 ? Rules(symbols[0], "minNotional") : null;
                }
                catch (VenueRefusedException)
                {
                    // "Invalid symbol": Binance does not have the pair.
                    return null;
                }
            });
        }

        if (type != InstrumentType.Perp)
        {
            return null;
        }

        Dictionary<string, VenueInstrument> all = await KeptAsync("perps", async () =>
        {
            JsonElement info = await SendAsync(null, FuturesHost, HttpMethod.Get, "/fapi/v1/exchangeInfo", string.Empty, cancellationToken);
            Dictionary<string, VenueInstrument> found = [];
            foreach (JsonElement listed in info.GetProperty("symbols").EnumerateArray())
            {
                if (Text(listed, "contractType") == "PERPETUAL" && Text(listed, "quoteAsset") == "USDT" && Rules(listed, "notional") is { } rules)
                {
                    found[rules.Symbol] = rules;
                }
            }

            return found;
        });
        return all.GetValueOrDefault(symbol);
    }

    private static VenueInstrument? Rules(JsonElement listed, string notionalField)
    {
        // A symbol on a break or being settled is still listed: its orders can be asked about and cancelled.
        bool trading = Text(listed, "status") == "TRADING";
        decimal step = 0m,
            tick = 0m,
            smallest = 0m,
            notional = 0m;
        foreach (JsonElement filter in listed.GetProperty("filters").EnumerateArray())
        {
            switch (Text(filter, "filterType"))
            {
                case "LOT_SIZE":
                    step = Number(filter, "stepSize");
                    smallest = Number(filter, "minQty");
                    break;
                case "PRICE_FILTER":
                    tick = Number(filter, "tickSize");
                    break;
                case "NOTIONAL" or "MIN_NOTIONAL":
                    notional = Math.Max(notional, Math.Max(Number(filter, notionalField), Number(filter, "minNotional")));
                    break;
            }
        }

        return step > 0m && tick > 0m ? new VenueInstrument(Text(listed, "symbol")!, step, tick, smallest, notional, trading) : null;
    }

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        bool spot = order.Type == InstrumentType.Spot;
        bool postOnly = order.TimeInForce == TimeInForce.PostOnly;
        List<string> fields = [$"symbol={instrument.Symbol}", $"side={(order.Side == OrderSide.Buy ? "BUY" : "SELL")}"];
        if (spot && postOnly)
        {
            // Spot has its own order type for "maker only"; it takes no time in force.
            fields.Add("type=LIMIT_MAKER");
        }
        else
        {
            fields.Add("type=LIMIT");
            fields.Add($"timeInForce={(order.TimeInForce == TimeInForce.ImmediateOrCancel ? "IOC" : postOnly ? "GTX" : "GTC")}");
        }

        fields.Add($"quantity={Plain(order.Quantity)}");
        fields.Add($"price={Plain(price)}");
        fields.Add($"newClientOrderId={order.ClientId}");
        fields.Add("newOrderRespType=ACK");
        if (!spot)
        {
            if (order.ReduceOnly)
            {
                fields.Add("reduceOnly=true");
            }
            else
            {
                await SetLeverageAsync(credentials, instrument.Symbol, order, cancellationToken);
            }
        }

        JsonElement placed = await SendAsync(credentials, spot ? SpotHost : FuturesHost, HttpMethod.Post, spot ? "/api/v3/order" : "/fapi/v1/order", string.Join('&', fields), cancellationToken);
        return Text(placed, "orderId");
    }

    /// <summary>Margin mode and leverage are ours to set, never whatever the account was left at. Nothing has been sent if this is refused.</summary>
    private async Task SetLeverageAsync(VenueCredentials credentials, string symbol, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        int leverage = (int)Math.Clamp(Math.Floor(order.Leverage), 1m, 125m);
        if (_set.TryGetValue((credentials.ApiKey, symbol), out (int Leverage, MarginMode Mode) set) && set == (leverage, order.MarginMode))
        {
            return;
        }

        try
        {
            await SendAsync(credentials, FuturesHost, HttpMethod.Post, "/fapi/v1/marginType", $"symbol={symbol}&marginType={(order.MarginMode == MarginMode.Cross ? "CROSSED" : "ISOLATED")}", cancellationToken);
        }
        catch (VenueRefusedException e) when (e.Message.Contains("(code -4046)", StringComparison.Ordinal))
        {
            // "No need to change margin type": it is already what we want.
        }

        await SendAsync(credentials, FuturesHost, HttpMethod.Post, "/fapi/v1/leverage", $"symbol={symbol}&leverage={leverage.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        _set[(credentials.ApiKey, symbol)] = (leverage, order.MarginMode);
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken) =>
        await SendAsync(credentials, type == InstrumentType.Spot ? SpotHost : FuturesHost, HttpMethod.Delete, type == InstrumentType.Spot ? "/api/v3/order" : "/fapi/v1/order", $"symbol={instrument.Symbol}&origClientOrderId={order.ClientId}", cancellationToken);

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        bool spot = type == InstrumentType.Spot;
        string host = spot ? SpotHost : FuturesHost;
        JsonElement found;
        try
        {
            found = await SendAsync(credentials, host, HttpMethod.Get, spot ? "/api/v3/order" : "/fapi/v1/order", $"symbol={instrument.Symbol}&origClientOrderId={order.ClientId}", cancellationToken);
        }
        catch (VenueRefusedException e) when (e.Message.Contains("(code -2013)", StringComparison.Ordinal))
        {
            // "Order does not exist."
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        string? id = Text(found, "orderId");
        bool done = Final.Contains(Text(found, "status") ?? string.Empty);
        decimal filled = Number(found, "executedQty");
        decimal value = Number(found, spot ? "cummulativeQuoteQty" : "cumQuote");
        decimal? fees = null;
        if (done && filled > 0m && id is not null)
        {
            fees = await FeesAsync(credentials, host, spot ? "/api/v3/myTrades" : "/fapi/v1/userTrades", instrument.Symbol, id, value / filled, cancellationToken);
        }

        return new VenueOrderReport(done ? VenueOrderState.Done : VenueOrderState.Open, filled, value, fees, id);
    }

    /// <summary>What a finished order paid in fees, from its trades; nothing while Binance does not list them or charged them in its own token.</summary>
    private async Task<decimal?> FeesAsync(VenueCredentials credentials, string host, string path, string symbol, string orderId, decimal price, CancellationToken cancellationToken)
    {
        try
        {
            JsonElement trades = await SendAsync(credentials, host, HttpMethod.Get, path, $"symbol={symbol}&orderId={orderId}", cancellationToken);
            return trades.ValueKind != JsonValueKind.Array || trades.GetArrayLength() == 0
                ? null
                : FeesInDollars(trades.EnumerateArray().Select(t => (Number(t, "commission"), Text(t, "commissionAsset"))), symbol[..^4], price);
        }
        catch (Exception e) when (e is VenueRefusedException or VenueUnclearException)
        {
            return null;
        }
    }

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement account = await SendAsync(credentials, SpotHost, HttpMethod.Get, "/api/v3/account", "omitZeroBalances=true", cancellationToken);
        JsonElement tickers = await SendAsync(null, SpotHost, HttpMethod.Get, "/api/v3/ticker/price", string.Empty, cancellationToken);
        Dictionary<string, decimal> prices = [];
        foreach (JsonElement ticker in tickers.EnumerateArray())
        {
            string symbol = Text(ticker, "symbol") ?? string.Empty;
            if (symbol.EndsWith("USDT", StringComparison.Ordinal))
            {
                prices[symbol[..^4]] = Number(ticker, "price");
            }
        }

        decimal cash = 0m;
        List<VenueHolding> holdings = [];
        foreach (JsonElement balance in account.GetProperty("balances").EnumerateArray())
        {
            string asset = (Text(balance, "asset") ?? string.Empty).ToUpperInvariant();
            decimal held = Number(balance, "free") + Number(balance, "locked");
            if (held <= 0m)
            {
                continue;
            }

            if (IsDollar(asset))
            {
                cash += held;
            }
            else
            {
                holdings.Add(new VenueHolding(asset, InstrumentType.Spot, held, null, null, prices.TryGetValue(asset, out decimal price) ? price : null));
            }
        }

        decimal? futuresEquity = null;
        bool perpsRead = true;
        try
        {
            futuresEquity = Number(await SendAsync(credentials, FuturesHost, HttpMethod.Get, "/fapi/v3/account", string.Empty, cancellationToken), "totalMarginBalance");
            foreach (JsonElement position in (await SendAsync(credentials, FuturesHost, HttpMethod.Get, "/fapi/v3/positionRisk", string.Empty, cancellationToken)).EnumerateArray())
            {
                string symbol = Text(position, "symbol") ?? string.Empty;
                decimal amount = Number(position, "positionAmt");
                if (amount != 0m && symbol.EndsWith("USDT", StringComparison.Ordinal))
                {
                    // In hedge mode a short is reported as a positive amount on its own side.
                    amount = Text(position, "positionSide") == "SHORT" ? -Math.Abs(amount) : amount;
                    decimal liquidation = Number(position, "liquidationPrice");
                    holdings.Add(new VenueHolding(symbol[..^4], InstrumentType.Perp, amount, Number(position, "entryPrice"), liquidation > 0m ? liquidation : null, Number(position, "markPrice")));
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

    /// <summary>One request. Everything travels in the address, signed as it is sent.</summary>
    private async Task<JsonElement> SendAsync(VenueCredentials? credentials, string host, HttpMethod method, string path, string fields, CancellationToken cancellationToken)
    {
        string query = fields;
        if (credentials is not null)
        {
            query += $"{(query.Length > 0 ? "&" : string.Empty)}recvWindow=5000&timestamp={NowMs.ToString(CultureInfo.InvariantCulture)}";
            query += "&signature=" + HmacHex(credentials.ApiSecret, query);
        }

        using HttpRequestMessage request = new(method, host + path + (query.Length > 0 ? "?" + query : string.Empty));
        if (credentials is not null)
        {
            request.Headers.Add("X-MBX-APIKEY", credentials.ApiKey);
        }

        (HttpStatusCode status, string text) = await ExchangeAsync(request, cancellationToken);
        JsonElement? answer = Json(text);
        if ((int)status is >= 200 and < 300 && answer is { } good)
        {
            return good;
        }

        string code = answer is { ValueKind: JsonValueKind.Object } failed ? Text(failed, "code") ?? string.Empty : string.Empty;
        string reason = $"{Limited(answer is { ValueKind: JsonValueKind.Object } said ? Text(said, "msg") : null)} (code {(code.Length == 0 ? ((int)status).ToString(CultureInfo.InvariantCulture) : code)})";
        if (NotAnAnswer(status) || code.Length == 0 || NotAnswers.Contains(code))
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }
}

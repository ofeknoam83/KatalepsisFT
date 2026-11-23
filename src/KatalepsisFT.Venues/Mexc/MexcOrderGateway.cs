using System.Globalization;
using System.Net;
using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Orders;

namespace KatalepsisFT.Venues.Mexc;

/// <summary>
/// Real orders at MEXC: spot against USDT, which is what Katalepsis trades there. The calls are those of
/// Katalepsis' Infrastructure/Services/Mexc/MexcApiClient.cs (commit 9847e4f): everything in the address, signed
/// as it is sent, with the same order types ("LIMIT", "LIMIT_MAKER" for maker only). MEXC's perpetuals are in the
/// book for prices, but orders for them are not sent: Katalepsis does not trade them, so there is nothing of its to follow.
/// </summary>
public sealed class MexcOrderGateway(HttpClient http, TimeProvider clock) : RestOrderGateway(http, clock), IOrderGateway
{
    public const string HttpClientName = "mexc-orders";
    public const string Host = "https://api.mexc.com";
    private static readonly string[] Final = ["FILLED", "CANCELED", "PARTIALLY_CANCELED", "REJECTED", "EXPIRED"];

    public Venue Venue => Venue.Mexc;

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        if (type != InstrumentType.Spot)
        {
            return null;
        }

        string symbol = asset.ToUpperInvariant() + "USDT";
        return await KeptAsync<VenueInstrument?>(symbol, async () =>
        {
            JsonElement info;
            try
            {
                info = await SendAsync(null, HttpMethod.Get, "/api/v3/exchangeInfo", $"symbol={symbol}", cancellationToken);
            }
            catch (VenueRefusedException)
            {
                return null;
            }

            if (!info.TryGetProperty("symbols", out JsonElement symbols) || symbols.ValueKind != JsonValueKind.Array || symbols.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement listed = symbols[0];
            bool open = Text(listed, "status") == "1" && listed.TryGetProperty("isSpotTradingAllowed", out JsonElement allowed) && allowed.ValueKind == JsonValueKind.True;
            decimal step = Number(listed, "baseSizePrecision");
            // Prices come with a number of decimals, not a step.
            decimal tick = 1m;
            for (int i = 0; i < (int)Math.Clamp(Number(listed, "quotePrecision"), 0m, 18m); i++)
            {
                tick /= 10m;
            }

            return !open || step <= 0m ? null : new VenueInstrument(symbol, step, tick, step, Number(listed, "quoteAmountPrecision"));
        });
    }

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        // At MEXC "how long" is part of the order type.
        string type = order.TimeInForce switch
        {
            TimeInForce.PostOnly => "LIMIT_MAKER",
            TimeInForce.ImmediateOrCancel => "IMMEDIATE_OR_CANCEL",
            _ => "LIMIT",
        };
        string fields = $"symbol={instrument.Symbol}&side={(order.Side == OrderSide.Buy ? "BUY" : "SELL")}&type={type}&quantity={Plain(order.Quantity)}&price={Plain(price)}&newClientOrderId={order.ClientId}";
        return Text(await SendAsync(credentials, HttpMethod.Post, "/api/v3/order", fields, cancellationToken), "orderId");
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken) =>
        await SendAsync(credentials, HttpMethod.Delete, "/api/v3/order", $"symbol={instrument.Symbol}&{Which(order)}", cancellationToken);

    private static string Which(OrderNames order) => order.VenueId is { Length: > 0 } id ? $"orderId={Uri.EscapeDataString(id)}" : $"origClientOrderId={order.ClientId}";

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        JsonElement found;
        try
        {
            found = await SendAsync(credentials, HttpMethod.Get, "/api/v3/order", $"symbol={instrument.Symbol}&{Which(order)}", cancellationToken);
        }
        catch (VenueRefusedException e) when (e.Message.Contains("(code -2013)", StringComparison.Ordinal))
        {
            // "Order does not exist."
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        string? id = Text(found, "orderId");
        bool done = Final.Contains(Text(found, "status") ?? string.Empty);
        decimal filled = Number(found, "executedQty");
        decimal value = Number(found, "cummulativeQuoteQty");
        decimal? fees = null;
        if (done && filled > 0m && id is not null)
        {
            try
            {
                JsonElement trades = await SendAsync(credentials, HttpMethod.Get, "/api/v3/myTrades", $"symbol={instrument.Symbol}&orderId={Uri.EscapeDataString(id)}", cancellationToken);
                fees = trades.ValueKind != JsonValueKind.Array || trades.GetArrayLength() == 0 ? null : FeesInDollars(trades.EnumerateArray().Select(t => (Number(t, "commission"), Text(t, "commissionAsset"))), instrument.Symbol[..^4], value / filled);
            }
            catch (Exception e) when (e is VenueRefusedException or VenueUnclearException)
            {
            }
        }

        return new VenueOrderReport(done ? VenueOrderState.Done : VenueOrderState.Open, filled, value, fees, id);
    }

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement account = await SendAsync(credentials, HttpMethod.Get, "/api/v3/account", string.Empty, cancellationToken);
        JsonElement tickers = await SendAsync(null, HttpMethod.Get, "/api/v3/ticker/price", string.Empty, cancellationToken);
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

        return new VenueAccountReport(cash, null, holdings);
    }

    private async Task<JsonElement> SendAsync(VenueCredentials? credentials, HttpMethod method, string path, string fields, CancellationToken cancellationToken)
    {
        string query = fields;
        if (credentials is not null)
        {
            query += $"{(query.Length > 0 ? "&" : string.Empty)}timestamp={NowMs.ToString(CultureInfo.InvariantCulture)}";
            query += "&signature=" + HmacHex(credentials.ApiSecret, query);
        }

        using HttpRequestMessage request = new(method, Host + path + (query.Length > 0 ? "?" + query : string.Empty));
        if (credentials is not null)
        {
            request.Headers.Add("X-MEXC-APIKEY", credentials.ApiKey);
        }

        (HttpStatusCode status, string text) = await ExchangeAsync(request, cancellationToken);
        JsonElement? answer = Json(text);
        if ((int)status is >= 200 and < 300 && answer is { } good && !(good.ValueKind == JsonValueKind.Object && good.TryGetProperty("code", out JsonElement c) && good.TryGetProperty("msg", out _) && Number(c) is not (0m or 200m)))
        {
            return good;
        }

        string code = answer is { ValueKind: JsonValueKind.Object } failed ? Text(failed, "code") ?? string.Empty : string.Empty;
        string reason = $"{Limited(answer is { ValueKind: JsonValueKind.Object } said ? Text(said, "msg") : null)} (code {(code.Length == 0 ? ((int)status).ToString(CultureInfo.InvariantCulture) : code)})";
        if (NotAnAnswer(status) || code.Length == 0)
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }
}

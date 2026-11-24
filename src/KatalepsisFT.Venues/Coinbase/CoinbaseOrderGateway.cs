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
using KatalepsisFT.Venues.Orders;

namespace KatalepsisFT.Venues.Coinbase;

/// <summary>
/// Real orders at Coinbase Exchange: spot against the dollar. The calls are those Katalepsis uses
/// (Infrastructure/Services/Coinbase/Exchange/CoinbaseExchangeApiClient.cs, commit 9847e4f): the same address,
/// the same signature and the same order fields, with our own name for each order added so that an order can be
/// found again when the answer to sending it was lost.
///
/// Coinbase wants that name as a UUID, so ours is turned into one, always the same one for the same name. Coinbase
/// forgets an order that was cancelled with nothing filled; asked about it later it says "not found".
/// </summary>
public sealed class CoinbaseOrderGateway(HttpClient http, TimeProvider clock) : RestOrderGateway(http, clock), IOrderGateway
{
    public const string HttpClientName = "coinbase-orders";
    public const string Host = "https://api.exchange.coinbase.com";

    public Venue Venue => Venue.CoinbaseExchange;

    /// <summary>Our name for an order as the UUID Coinbase will know it by.</summary>
    public static string ClientOid(string clientId) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(clientId))).ToString("D");

    private static string Which(OrderNames order) => order.VenueId is { Length: > 0 } id ? Uri.EscapeDataString(id) : "client:" + ClientOid(order.ClientId);

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        if (type != InstrumentType.Spot)
        {
            return null;
        }

        string product = asset.ToUpperInvariant() + "-USD";
        return await KeptAsync<VenueInstrument?>(product, async () =>
        {
            JsonElement listed;
            try
            {
                listed = await SendAsync(null, HttpMethod.Get, $"/products/{product}", null, cancellationToken);
            }
            catch (VenueRefusedException)
            {
                return null;
            }

            bool open = Text(listed, "status") == "online" && !Flag(listed, "trading_disabled") && !Flag(listed, "cancel_only");
            decimal step = Number(listed, "base_increment");
            decimal tick = Number(listed, "quote_increment");
            return !open || step <= 0m || tick <= 0m ? null : new VenueInstrument(product, step, tick, step, Number(listed, "min_market_funds"));
        });
    }

    private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        Dictionary<string, object> body = new()
        {
            ["type"] = "limit",
            ["side"] = order.Side == OrderSide.Buy ? "buy" : "sell",
            ["product_id"] = instrument.Symbol,
            ["price"] = Plain(price),
            ["size"] = Plain(order.Quantity),
            ["time_in_force"] = order.TimeInForce == TimeInForce.ImmediateOrCancel ? "IOC" : "GTC",
            ["client_oid"] = ClientOid(order.ClientId),
        };
        if (order.TimeInForce == TimeInForce.PostOnly)
        {
            body["post_only"] = true;
        }

        return Text(await SendAsync(credentials, HttpMethod.Post, "/orders", body, cancellationToken), "id");
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken) =>
        await SendAsync(credentials, HttpMethod.Delete, $"/orders/{Which(order)}", null, cancellationToken);

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        JsonElement o;
        try
        {
            o = await SendAsync(credentials, HttpMethod.Get, $"/orders/{Which(order)}", null, cancellationToken);
        }
        catch (VenueRefusedException e) when (e.Message.Contains("(code 404)", StringComparison.Ordinal))
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        if (o.ValueKind != JsonValueKind.Object)
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        string status = Text(o, "status") ?? string.Empty;
        decimal filled = Number(o, "filled_size");
        // Fees at Coinbase are charged in the quote currency, the dollar.
        return new VenueOrderReport(status is "done" or "rejected" ? VenueOrderState.Done : VenueOrderState.Open, filled, Number(o, "executed_value"), filled > 0m ? Number(o, "fill_fees") : null, Text(o, "id"), Text(o, "reject_reason") is { Length: > 0 } why ? why : null);
    }

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement accounts = await SendAsync(credentials, HttpMethod.Get, "/accounts", null, cancellationToken);
        Dictionary<string, decimal> perDollar = [];
        try
        {
            // How much of each coin one dollar buys, from Coinbase's public price list.
            using HttpRequestMessage request = new(HttpMethod.Get, "https://api.coinbase.com/v2/exchange-rates?currency=USD");
            request.Headers.TryAddWithoutValidation("User-Agent", "KatalepsisFT");
            (HttpStatusCode status, string text) = await ExchangeAsync(request, cancellationToken);
            if ((int)status is >= 200 and < 300 && Json(text) is { } answer && answer.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("rates", out JsonElement rates))
            {
                foreach (JsonProperty rate in rates.EnumerateObject())
                {
                    perDollar[rate.Name.ToUpperInvariant()] = Number(rate.Value);
                }
            }
        }
        catch (VenueUnclearException)
        {
            // Without prices the holdings are still what they are; they are only not valued.
        }

        decimal cash = 0m;
        List<VenueHolding> holdings = [];
        foreach (JsonElement account in accounts.EnumerateArray())
        {
            string currency = (Text(account, "currency") ?? string.Empty).ToUpperInvariant();
            decimal held = Number(account, "balance");
            if (held <= 0m)
            {
                continue;
            }

            if (IsDollar(currency))
            {
                cash += held;
            }
            else
            {
                holdings.Add(new VenueHolding(currency, InstrumentType.Spot, held, null, null, perDollar.TryGetValue(currency, out decimal rate) && rate > 0m ? 1m / rate : null));
            }
        }

        return new VenueAccountReport(cash, null, holdings);
    }

    private async Task<JsonElement> SendAsync(VenueCredentials? credentials, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        string json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        using HttpRequestMessage request = new(method, Host + path);
        request.Headers.TryAddWithoutValidation("User-Agent", "KatalepsisFT");
        if (json.Length > 0)
        {
            request.Content = Body(json);
        }

        if (credentials is not null)
        {
            byte[] secret;
            try
            {
                // Coinbase hands the secret out encoded; it is used decoded.
                secret = Convert.FromBase64String(credentials.ApiSecret);
            }
            catch (FormatException)
            {
                throw new VenueRefusedException("the key's secret is not in the form Coinbase gives it out in");
            }

            string timestamp = Clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            request.Headers.Add("CB-ACCESS-KEY", credentials.ApiKey);
            request.Headers.Add("CB-ACCESS-PASSPHRASE", credentials.Passphrase ?? string.Empty);
            request.Headers.Add("CB-ACCESS-SIGN", Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(timestamp + method.Method.ToUpperInvariant() + path + json))));
            request.Headers.Add("CB-ACCESS-TIMESTAMP", timestamp);
        }

        (HttpStatusCode status, string text) = await ExchangeAsync(request, cancellationToken);
        JsonElement? answer = Json(text);
        if ((int)status is >= 200 and < 300 && answer is { } good)
        {
            return good;
        }

        string reason = $"{Limited(answer is { ValueKind: JsonValueKind.Object } said ? Text(said, "message") : null)} (code {((int)status).ToString(CultureInfo.InvariantCulture)})";
        // A page from something in front of Coinbase (a block, a missing route) carries no message of Coinbase's own
        // and says nothing about the order.
        bool inItsOwnWords = answer is { ValueKind: JsonValueKind.Object } own && Text(own, "message") is { Length: > 0 };
        if (NotAnAnswer(status) || (int)status is >= 200 and < 300 || !inItsOwnWords)
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }
}

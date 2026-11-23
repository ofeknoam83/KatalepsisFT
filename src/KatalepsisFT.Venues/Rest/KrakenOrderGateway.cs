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

namespace KatalepsisFT.Venues.Rest;

/// <summary>
/// Real orders at Kraken: spot against the dollar. Katalepsis has no trading client for Kraken, so this follows
/// Kraken's own description.
///
/// Kraken answers "all fine" at the level of the connection even when it refuses a request; its verdict is in the
/// answer. It can only look an order up by its own id, so an order whose answer was lost is looked for by our name
/// among the open orders and then among the closed ones. Kraken calls bitcoin XBT and dogecoin XDG. Fees are asked
/// for in dollars on every order.
/// </summary>
public sealed class KrakenOrderGateway(HttpClient http, TimeProvider clock) : RestOrderGateway(http, clock), IOrderGateway
{
    public const string HttpClientName = "kraken-orders";
    public const string Host = "https://api.kraken.com";

    /// <summary>Kraken's answers that do not say whether the request was carried out. Of the first it says itself "the order may have been placed".</summary>
    private static readonly string[] NotAnswers = ["EService:Deadline elapsed", "EService:Unavailable", "EService:Busy", "EService: Throttled", "EService:Throttled", "EGeneral:Internal error", "EAPI:Rate limit exceeded", "EOrder:Rate limit exceeded", "EGeneral:Temporary lockout"];
    private static readonly string[] Final = ["closed", "canceled", "expired"];
    private static readonly string[] OldNames = ["XXBT", "XETH", "XXDG", "XXRP", "XLTC", "XXLM", "XETC", "XZEC", "XXMR", "XREP", "XMLN", "ZUSD", "ZEUR", "ZGBP", "ZCAD", "ZJPY", "ZAUD"];
    private static long _lastNonce;

    public Venue Venue => Venue.Kraken;

    /// <summary>Kraken's name for an asset where it differs from everyone else's.</summary>
    private static string Theirs(string asset) =>
        asset.ToUpperInvariant() switch
        {
            "BTC" => "XBT",
            "DOGE" => "XDG",
            string other => other,
        };

    /// <summary>An asset as Kraken names it in a balance ("XXBT", "ZUSD", "USDT.F") under its usual name.</summary>
    public static string Ours(string theirs)
    {
        string name = theirs.ToUpperInvariant();
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        name = dot > 0 ? name[..dot] : name;
        // Its oldest assets carry a letter in front: X for coins, Z for money. Only those: newer names are as they are.
        name = OldNames.Contains(name) ? name[1..] : name;
        return name switch
        {
            "XBT" => "BTC",
            "XDG" => "DOGE",
            _ => name,
        };
    }

    /// <summary>Our name for an order as the UUID Kraken will know it by; its other form allows only eighteen characters.</summary>
    public static string ClientOrderId(string clientId) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(clientId))).ToString("D");

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        if (type != InstrumentType.Spot)
        {
            return null;
        }

        string pair = Theirs(asset) + "USD";
        return await KeptAsync<VenueInstrument?>(pair, async () =>
        {
            JsonElement result;
            try
            {
                result = await PublicAsync($"/0/public/AssetPairs?pair={pair}", cancellationToken);
            }
            catch (VenueRefusedException)
            {
                // "Unknown asset pair"
                return null;
            }

            // The answer is under Kraken's internal name for the pair, which is not the one that was asked for.
            JsonElement listed = result.ValueKind == JsonValueKind.Object ? result.EnumerateObject().Select(p => p.Value).FirstOrDefault() : default;
            if (listed.ValueKind != JsonValueKind.Object || Text(listed, "status") != "online")
            {
                return null;
            }

            decimal step = 1m;
            for (int i = 0; i < (int)Math.Clamp(Number(listed, "lot_decimals"), 0m, 18m); i++)
            {
                step /= 10m;
            }

            decimal tick = Number(listed, "tick_size");
            return tick <= 0m ? null : new VenueInstrument(Text(listed, "altname") ?? pair, step, tick, Number(listed, "ordermin"), Number(listed, "costmin"));
        });
    }

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        List<(string, string)> fields =
        [
            ("ordertype", "limit"),
            ("type", order.Side == OrderSide.Buy ? "buy" : "sell"),
            ("volume", Plain(order.Quantity)),
            ("pair", instrument.Symbol),
            ("price", Plain(price)),
            ("cl_ord_id", ClientOrderId(order.ClientId)),
            ("timeinforce", order.TimeInForce == TimeInForce.ImmediateOrCancel ? "IOC" : "GTC"),
            // The fee in dollars, whichever side; and "post" for maker only.
            ("oflags", order.TimeInForce == TimeInForce.PostOnly ? "fciq,post" : "fciq"),
        ];
        JsonElement result = await PrivateAsync(credentials, "/0/private/AddOrder", fields, cancellationToken);
        return result.TryGetProperty("txid", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0 ? ids[0].GetString() : null;
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken) =>
        await PrivateAsync(credentials, "/0/private/CancelOrder", [order.VenueId is { Length: > 0 } id ? ("txid", id) : ("cl_ord_id", ClientOrderId(order.ClientId))], cancellationToken);

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        (string Id, JsonElement Order)? found = null;
        if (order.VenueId is { Length: > 0 } id)
        {
            try
            {
                JsonElement result = await PrivateAsync(credentials, "/0/private/QueryOrders", [("txid", id)], cancellationToken);
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(id, out JsonElement one))
                {
                    found = (id, one);
                }
            }
            catch (VenueRefusedException e) when (e.Message.Contains("Unknown order", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("Invalid order", StringComparison.OrdinalIgnoreCase))
            {
            }
        }
        else
        {
            // Kraken looks an order up only by its own id. Without it, ours is looked for among the open orders and then the closed.
            string ours = ClientOrderId(order.ClientId);
            found = First(await PrivateAsync(credentials, "/0/private/OpenOrders", [("cl_ord_id", ours)], cancellationToken), "open", ours)
                ?? First(await PrivateAsync(credentials, "/0/private/ClosedOrders", [("cl_ord_id", ours)], cancellationToken), "closed", ours);
        }

        if (found is not { } o)
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        decimal filled = Number(o.Order, "vol_exec");
        return new VenueOrderReport(Final.Contains(Text(o.Order, "status") ?? string.Empty) ? VenueOrderState.Done : VenueOrderState.Open, filled, Number(o.Order, "cost"), filled > 0m ? Number(o.Order, "fee") : null, o.Id, Text(o.Order, "reason") is { Length: > 0 } why && why != "User requested" ? why : null);
    }

    /// <summary>The order with our name in a list of orders that Kraken keys by its own ids.</summary>
    private static (string Id, JsonElement Order)? First(JsonElement result, string list, string ours)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(list, out JsonElement orders) || orders.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty order in orders.EnumerateObject())
        {
            if (string.Equals(Text(order.Value, "cl_ord_id"), ours, StringComparison.OrdinalIgnoreCase))
            {
                return (order.Name, order.Value);
            }
        }

        return null;
    }

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement balances = await PrivateAsync(credentials, "/0/private/Balance", [], cancellationToken);
        Dictionary<string, decimal> prices = [];
        try
        {
            // Every pair's last price in one call, under ordinary names ("BTC/USD").
            JsonElement tickers = await PublicAsync("/0/public/Ticker?assetVersion=1", cancellationToken);
            foreach (JsonProperty ticker in tickers.EnumerateObject())
            {
                if (ticker.Name.EndsWith("/USD", StringComparison.Ordinal) && ticker.Value.TryGetProperty("c", out JsonElement last) && last.ValueKind == JsonValueKind.Array && last.GetArrayLength() > 0)
                {
                    prices[Ours(ticker.Name[..^4])] = Number(last[0]);
                }
            }
        }
        catch (Exception e) when (e is VenueRefusedException or VenueUnclearException)
        {
            // Without prices the holdings are still what they are; they are only not valued.
        }

        decimal cash = 0m;
        Dictionary<string, decimal> held = [];
        foreach (JsonProperty balance in balances.EnumerateObject())
        {
            decimal amount = Number(balance.Value);
            if (amount <= 0m)
            {
                continue;
            }

            // Balances that are earning ("USDT.F") count with the asset itself.
            string asset = Ours(balance.Name);
            if (IsDollar(asset))
            {
                cash += amount;
            }
            else
            {
                held[asset] = held.GetValueOrDefault(asset) + amount;
            }
        }

        return new VenueAccountReport(cash, null, held.Select(h => new VenueHolding(h.Key, InstrumentType.Spot, h.Value, null, null, prices.TryGetValue(h.Key, out decimal price) ? price : null)).ToList());
    }

    private async Task<JsonElement> PublicAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Host + pathAndQuery);
        request.Headers.TryAddWithoutValidation("User-Agent", "KatalepsisFT");
        return Verdict(await ExchangeAsync(request, cancellationToken));
    }

    private async Task<JsonElement> PrivateAsync(VenueCredentials credentials, string path, IReadOnlyList<(string Name, string Value)> fields, CancellationToken cancellationToken)
    {
        // A number that only ever goes up for the key: the clock in milliseconds, pushed on by one when two requests share a millisecond.
        long now = NowMs;
        long nonce;
        long last;
        do
        {
            last = Interlocked.Read(ref _lastNonce);
            nonce = Math.Max(now, last + 1);
        } while (Interlocked.CompareExchange(ref _lastNonce, nonce, last) != last);

        string text = nonce.ToString(CultureInfo.InvariantCulture);
        string post = string.Join('&', new[] { ("nonce", text) }.Concat(fields).Select(f => $"{f.Item1}={Uri.EscapeDataString(f.Item2)}"));
        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(credentials.ApiSecret);
        }
        catch (FormatException)
        {
            throw new VenueRefusedException("the key's secret is not in the form Kraken gives it out in");
        }


        using HttpRequestMessage request = new(HttpMethod.Post, Host + path) { Content = new StringContent(post, Encoding.UTF8, "application/x-www-form-urlencoded") };
        request.Headers.TryAddWithoutValidation("User-Agent", "KatalepsisFT");
        request.Headers.Add("API-Key", credentials.ApiKey);
        request.Headers.Add("API-Sign", Sign(secret, path, text, post));
        return Verdict(await ExchangeAsync(request, cancellationToken));
    }

    /// <summary>Kraken's signature: the address, then the fingerprint of the nonce and the body, signed with the decoded secret.</summary>
    public static string Sign(byte[] secret, string path, string nonce, string post)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + post));
        byte[] message = new byte[pathBytes.Length + digest.Length];
        pathBytes.CopyTo(message, 0);
        digest.CopyTo(message, pathBytes.Length);
        return Convert.ToBase64String(HMACSHA512.HashData(secret, message));
    }

    /// <summary>Kraken's verdict on a request, which is in the answer and not in how the answer arrived.</summary>
    private static JsonElement Verdict((HttpStatusCode Status, string Text) exchanged)
    {
        JsonElement? answer = Json(exchanged.Text);
        List<string> errors = answer is { ValueKind: JsonValueKind.Object } read && read.TryGetProperty("error", out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(e => e.StartsWith('E')).ToList()
            : [];
        if (errors.Count == 0 && (int)exchanged.Status is >= 200 and < 300 && answer is { ValueKind: JsonValueKind.Object } good && good.TryGetProperty("result", out JsonElement result))
        {
            return result;
        }

        string reason = errors.Count > 0 ? Limited(string.Join("; ", errors)) : $"no verdict (code {((int)exchanged.Status).ToString(CultureInfo.InvariantCulture)})";
        if (errors.Count == 0 || NotAnAnswer(exchanged.Status) || errors.Any(e => NotAnswers.Any(n => e.StartsWith(n, StringComparison.OrdinalIgnoreCase))))
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }
}

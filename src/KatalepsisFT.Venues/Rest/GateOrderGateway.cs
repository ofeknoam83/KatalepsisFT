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
using KatalepsisFT.Venues.Orders;

namespace KatalepsisFT.Venues.Rest;

/// <summary>
/// Real orders at Gate (version 4 of its interface): spot against USDT and USDT-settled perpetuals. Katalepsis does
/// not trade on Gate, so this follows Gate's own description.
///
/// A perpetual is traded in whole contracts whose sign is the side, so quantities are turned into contracts on the
/// way out and back on the way in. Gate can only look an order up by our name for it while the order is open (and
/// for a minute after, for perpetuals). So Gate's own id is used as soon as it is known, and an order whose answer
/// was lost is also looked for among the recently finished ones.
/// </summary>
public sealed class GateOrderGateway(HttpClient http, TimeProvider clock) : RestOrderGateway(http, clock), IOrderGateway
{
    public const string HttpClientName = "gate-orders";
    public const string Host = "https://api.gateio.ws";
    private const string Prefix = "/api/v4";

    /// <summary>Busy or failing inside.</summary>
    private static readonly string[] NotAnswers = ["TOO_BUSY", "INTERNAL", "SERVER_ERROR", "TOO_MANY_REQUESTS"];
    private static readonly string[] OrderUnknown = ["ORDER_NOT_FOUND"];
    private readonly ConcurrentDictionary<string, decimal> _contractSize = new();
    private readonly ConcurrentDictionary<(string Key, string Contract), (int Leverage, MarginMode Mode)> _set = new();

    public Venue Venue => Venue.Gate;

    /// <summary>Gate wants a name of ours to begin with "t-".</summary>
    private static string TextOf(string clientId) => "t-" + clientId;

    private static string Pair(string asset) => asset.ToUpperInvariant() + "_USDT";

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        if (type is not (InstrumentType.Spot or InstrumentType.Perp))
        {
            return null;
        }

        string pair = Pair(asset);
        return await KeptAsync<VenueInstrument?>($"{type}:{pair}", async () =>
        {
            JsonElement listed;
            try
            {
                listed = await SendAsync(null, HttpMethod.Get, type == InstrumentType.Spot ? $"/spot/currency_pairs/{pair}" : $"/futures/usdt/contracts/{pair}", string.Empty, null, cancellationToken);
            }
            catch (VenueRefusedException)
            {
                return null;
            }

            if (type == InstrumentType.Spot)
            {
                // Spot gives numbers of decimals, not steps.
                decimal spotStep = Step(Number(listed, "amount_precision"));
                return new VenueInstrument(pair, spotStep, Step(Number(listed, "precision")), Math.Max(spotStep, Number(listed, "min_base_amount")), Number(listed, "min_quote_amount"), Text(listed, "trade_status") == "tradable");
            }

            decimal contract = Number(listed, "quanto_multiplier");
            decimal tick = Number(listed, "order_price_round");
            bool leaving = listed.TryGetProperty("in_delisting", out JsonElement delisting) && delisting.ValueKind == JsonValueKind.True;
            if (contract <= 0m || tick <= 0m)
            {
                return null;
            }

            _contractSize[pair] = contract;
            // A contract being delisted takes no new orders, but a position in it is still there to be read and closed.
            return new VenueInstrument(pair, contract, tick, Math.Max(1m, Number(listed, "order_size_min")) * contract, 0m, !leaving);
        });
    }

    private static decimal Step(decimal places)
    {
        decimal step = 1m;
        for (int i = 0; i < (int)Math.Clamp(places, 0m, 18m); i++)
        {
            step /= 10m;
        }

        return step;
    }

    private decimal ContractSize(string contract) => _contractSize.TryGetValue(contract, out decimal size) ? size : throw new VenueRefusedException($"The size of one {contract} contract is not known.");

    private static string Tif(TimeInForce timeInForce) =>
        timeInForce switch
        {
            TimeInForce.ImmediateOrCancel => "ioc",
            // "Pending or cancelled": Gate's name for maker only.
            TimeInForce.PostOnly => "poc",
            _ => "gtc",
        };

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        if (order.Type == InstrumentType.Spot)
        {
            Dictionary<string, object> spot = new()
            {
                ["text"] = TextOf(order.ClientId),
                ["currency_pair"] = instrument.Symbol,
                ["type"] = "limit",
                ["account"] = "spot",
                ["side"] = order.Side == OrderSide.Buy ? "buy" : "sell",
                ["amount"] = Plain(order.Quantity),
                ["price"] = Plain(price),
                ["time_in_force"] = Tif(order.TimeInForce),
            };
            return Text(await SendAsync(credentials, HttpMethod.Post, "/spot/orders", string.Empty, spot, cancellationToken), "id");
        }

        decimal contracts = order.Quantity / ContractSize(instrument.Symbol);
        if (contracts != Math.Floor(contracts) || contracts < 1m)
        {
            throw new VenueRefusedException($"The quantity is not a whole number of contracts of {Plain(ContractSize(instrument.Symbol))}.");
        }

        if (!order.ReduceOnly)
        {
            int leverage = (int)Math.Clamp(Math.Floor(order.Leverage), 1m, 100m);
            if (!_set.TryGetValue((credentials.ApiKey, instrument.Symbol), out (int Leverage, MarginMode Mode) set) || set != (leverage, order.MarginMode))
            {
                // Leverage is ours to set. At Gate "0" means cross, with the limit given beside it. It travels in the
                // address, not in a body. Nothing has been sent if this is refused.
                string level = leverage.ToString(CultureInfo.InvariantCulture);
                await SendAsync(credentials, HttpMethod.Post, $"/futures/usdt/positions/{instrument.Symbol}/leverage", order.MarginMode == MarginMode.Cross ? $"leverage=0&cross_leverage_limit={level}" : $"leverage={level}", null, cancellationToken);
                _set[(credentials.ApiKey, instrument.Symbol)] = (leverage, order.MarginMode);
            }
        }

        Dictionary<string, object> perp = new()
        {
            ["contract"] = instrument.Symbol,
            // There is no side: a sell is a negative number of contracts.
            ["size"] = order.Side == OrderSide.Buy ? (long)contracts : -(long)contracts,
            ["price"] = Plain(price),
            ["tif"] = Tif(order.TimeInForce),
            ["text"] = TextOf(order.ClientId),
        };
        if (order.ReduceOnly)
        {
            perp["reduce_only"] = true;
        }

        return Text(await SendAsync(credentials, HttpMethod.Post, "/futures/usdt/orders", string.Empty, perp, cancellationToken), "id");
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        string which = Uri.EscapeDataString(order.VenueId is { Length: > 0 } id ? id : TextOf(order.ClientId));
        if (type == InstrumentType.Spot)
        {
            await SendAsync(credentials, HttpMethod.Delete, $"/spot/orders/{which}", $"currency_pair={instrument.Symbol}", null, cancellationToken);
        }
        else
        {
            await SendAsync(credentials, HttpMethod.Delete, $"/futures/usdt/orders/{which}", string.Empty, null, cancellationToken);
        }
    }

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        bool spot = type == InstrumentType.Spot;
        string which = Uri.EscapeDataString(order.VenueId is { Length: > 0 } id ? id : TextOf(order.ClientId));
        JsonElement? found = null;
        try
        {
            JsonElement one = spot
                ? await SendAsync(credentials, HttpMethod.Get, $"/spot/orders/{which}", $"currency_pair={instrument.Symbol}", null, cancellationToken)
                : await SendAsync(credentials, HttpMethod.Get, $"/futures/usdt/orders/{which}", string.Empty, null, cancellationToken);
            found = one.ValueKind == JsonValueKind.Object ? one : null;
        }
        catch (VenueRefusedException e) when (OrderUnknown.Any(label => e.Message.Contains($"(code {label})", StringComparison.Ordinal)))
        {
        }

        if (found is null && order.VenueId is null)
        {
            // Asked by our name alone, a finished order no longer answers. It is looked for among the latest finished ones.
            JsonElement finished = spot
                ? await SendAsync(credentials, HttpMethod.Get, "/spot/orders", $"currency_pair={instrument.Symbol}&status=finished&limit=100", null, cancellationToken)
                : await SendAsync(credentials, HttpMethod.Get, "/futures/usdt/orders", $"contract={instrument.Symbol}&status=finished&limit=100", null, cancellationToken);
            if (finished.ValueKind == JsonValueKind.Array)
            {
                string text = TextOf(order.ClientId);
                found = finished.EnumerateArray().Where(o => Text(o, "text") == text).Cast<JsonElement?>().FirstOrDefault();
            }
        }

        if (found is not { } o)
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        string? orderId = Text(o, "id");
        string asset = instrument.Symbol[..^5];
        if (spot)
        {
            decimal filled = o.TryGetProperty("filled_amount", out JsonElement amount) ? Number(amount) : Number(o, "amount") - Number(o, "left");
            decimal value = Number(o, "filled_total");
            // Paid partly in Gate's own token or in points: then there is no figure.
            decimal? fees = filled <= 0m || Number(o, "gt_fee") != 0m || Number(o, "point_fee") != 0m ? null : FeesInDollars([(Number(o, "fee"), Text(o, "fee_currency"))], asset, value / filled);
            return new VenueOrderReport(Text(o, "status") == "open" ? VenueOrderState.Open : VenueOrderState.Done, filled, value, fees, orderId, Reason(o));
        }

        decimal contract = ContractSize(instrument.Symbol);
        decimal traded = Math.Abs(Number(o, "size") - Number(o, "left")) * contract;
        decimal average = Number(o, "fill_price");
        bool done = Text(o, "status") == "finished";
        decimal? perpFees = null;
        if (done && traded > 0m && orderId is not null)
        {
            try
            {
                // The order carries fee rates only; what was paid is on its trades.
                JsonElement trades = await SendAsync(credentials, HttpMethod.Get, "/futures/usdt/my_trades", $"contract={instrument.Symbol}&order={Uri.EscapeDataString(orderId)}", null, cancellationToken);
                perpFees = trades.ValueKind != JsonValueKind.Array || trades.GetArrayLength() == 0 ? null : trades.EnumerateArray().Sum(t => Number(t, "fee"));
            }
            catch (Exception e) when (e is VenueRefusedException or VenueUnclearException)
            {
            }
        }

        return new VenueOrderReport(done ? VenueOrderState.Done : VenueOrderState.Open, traded, traded * average, perpFees, orderId, Reason(o));
    }

    /// <summary>Why Gate ended an order, when it was not simply filled or cancelled by us.</summary>
    private static string? Reason(JsonElement order) => Text(order, "finish_as") is { Length: > 0 } how && how is not ("open" or "filled" or "cancelled") ? how.Replace('_', ' ') : null;

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement accounts = await SendAsync(credentials, HttpMethod.Get, "/spot/accounts", string.Empty, null, cancellationToken);
        JsonElement tickers = await SendAsync(null, HttpMethod.Get, "/spot/tickers", string.Empty, null, cancellationToken);
        Dictionary<string, decimal> prices = [];
        foreach (JsonElement ticker in tickers.EnumerateArray())
        {
            string pair = Text(ticker, "currency_pair") ?? string.Empty;
            if (pair.EndsWith("_USDT", StringComparison.Ordinal))
            {
                prices[pair[..^5]] = Number(ticker, "last");
            }
        }

        decimal cash = 0m;
        List<VenueHolding> holdings = [];
        foreach (JsonElement coin in accounts.EnumerateArray())
        {
            string name = (Text(coin, "currency") ?? string.Empty).ToUpperInvariant();
            decimal held = Number(coin, "available") + Number(coin, "locked");
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
            JsonElement futures = await SendAsync(credentials, HttpMethod.Get, "/futures/usdt/accounts", string.Empty, null, cancellationToken);
            // Gate's "total" leaves open profit and loss out.
            futuresEquity = Number(futures, "total") + Number(futures, "unrealised_pnl");
            JsonElement positions = await SendAsync(credentials, HttpMethod.Get, "/futures/usdt/positions", "holding=true", null, cancellationToken);
            if (positions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement position in positions.EnumerateArray())
                {
                    string contract = Text(position, "contract") ?? string.Empty;
                    decimal size = Number(position, "size");
                    if (size == 0m || !contract.EndsWith("_USDT", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!_contractSize.ContainsKey(contract) && await InstrumentAsync(contract[..^5], InstrumentType.Perp, cancellationToken) is null)
                    {
                        continue;
                    }

                    decimal liquidation = Number(position, "liq_price");
                    holdings.Add(new VenueHolding(contract[..^5], InstrumentType.Perp, size * _contractSize[contract], Number(position, "entry_price"), liquidation > 0m ? liquidation : null, Number(position, "mark_price")));
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

    private async Task<JsonElement> SendAsync(VenueCredentials? credentials, HttpMethod method, string path, string query, object? body, CancellationToken cancellationToken)
    {
        string json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        using HttpRequestMessage request = new(method, Host + Prefix + path + (query.Length > 0 ? "?" + query : string.Empty));
        if (json.Length > 0)
        {
            request.Content = Body(json);
        }

        if (credentials is not null)
        {
            // Seconds, not milliseconds. The body goes into the signature as its own fingerprint, also when there is none.
            string timestamp = Clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            string bodyHash = Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            string signed = $"{method.Method.ToUpperInvariant()}\n{Prefix}{path}\n{query}\n{bodyHash}\n{timestamp}";
            request.Headers.Add("KEY", credentials.ApiKey);
            request.Headers.Add("Timestamp", timestamp);
            request.Headers.Add("SIGN", Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.UTF8.GetBytes(signed))).ToLowerInvariant());
        }

        (HttpStatusCode status, string text) = await ExchangeAsync(request, cancellationToken);
        JsonElement? answer = Json(text);
        if ((int)status is >= 200 and < 300 && answer is { } good)
        {
            return good;
        }

        // Gate names each kind of refusal; the wording beside it is sometimes under "message", sometimes "detail".
        string label = answer is { ValueKind: JsonValueKind.Object } failed ? Text(failed, "label") ?? string.Empty : string.Empty;
        string? message = answer is { ValueKind: JsonValueKind.Object } said ? Text(said, "message") ?? Text(said, "detail") : null;
        string reason = $"{Limited(message)} (code {(label.Length == 0 ? ((int)status).ToString(CultureInfo.InvariantCulture) : label)})";
        if (NotAnAnswer(status) || label.Length == 0 || NotAnswers.Contains(label) || (int)status is >= 200 and < 300)
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }
}

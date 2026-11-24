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

namespace KatalepsisFT.Venues.Okx;

/// <summary>
/// Real orders at OKX (version 5): spot against USDT and USDT-margined perpetual swaps, in one account with one key.
/// A swap is traded in contracts, so every quantity is turned into contracts on the way out and back into units of
/// the asset on the way in. OKX can answer "done" for a request and still refuse the order inside it; both are read.
/// The account must be in net (one-way) mode and, for swaps, in a mode that allows them; otherwise OKX refuses.
/// </summary>
public sealed class OkxOrderGateway(HttpClient http, TimeProvider clock) : RestOrderGateway(http, clock), IOrderGateway
{
    public const string HttpClientName = "okx-orders";
    public const string Host = "https://www.okx.com";

    /// <summary>Busy, timed out or limited; and "the order timed out", where it may or may not be in.</summary>
    private static readonly string[] NotAnswers = ["50001", "50004", "50011", "50013", "50026", "50061", "51149"];
    private static readonly string[] Final = ["filled", "canceled", "mmp_canceled"];
    private readonly ConcurrentDictionary<string, decimal> _contractSize = new();
    private readonly ConcurrentDictionary<(string Key, string Symbol), (int Leverage, MarginMode Mode)> _set = new();

    public Venue Venue => Venue.Okx;

    private static string Symbol(string asset, InstrumentType type) => type == InstrumentType.Spot ? $"{asset.ToUpperInvariant()}-USDT" : $"{asset.ToUpperInvariant()}-USDT-SWAP";

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        if (type is not (InstrumentType.Spot or InstrumentType.Perp))
        {
            return null;
        }

        string symbol = Symbol(asset, type);
        return await KeptAsync<VenueInstrument?>(symbol, async () =>
        {
            JsonElement data;
            try
            {
                data = await SendAsync(null, HttpMethod.Get, $"/api/v5/public/instruments?instType={(type == InstrumentType.Spot ? "SPOT" : "SWAP")}&instId={symbol}", null, false, cancellationToken);
            }
            catch (VenueRefusedException)
            {
                return null;
            }

            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return null;
            }

            // Suspended or being delisted: still listed, so its orders can be asked about and cancelled.
            bool trading = Text(data[0], "state") == "live";

            // A swap's steps are in contracts; one contract is "ctVal" units of the asset.
            decimal contract = type == InstrumentType.Spot ? 1m : Number(data[0], "ctVal");
            decimal step = Number(data[0], "lotSz") * contract;
            decimal tick = Number(data[0], "tickSz");
            if (contract <= 0m || step <= 0m || tick <= 0m)
            {
                return null;
            }

            _contractSize[symbol] = contract;
            return new VenueInstrument(symbol, step, tick, Number(data[0], "minSz") * contract, 0m, trading);
        });
    }

    private decimal ContractSize(VenueInstrument instrument) =>
        _contractSize.TryGetValue(instrument.Symbol, out decimal size) ? size : throw new VenueRefusedException($"The size of one {instrument.Symbol} contract is not known.");

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        bool spot = order.Type == InstrumentType.Spot;
        decimal size = order.Quantity / ContractSize(instrument);
        Dictionary<string, object> body = new()
        {
            ["instId"] = instrument.Symbol,
            ["tdMode"] = spot ? "cash" : order.MarginMode == MarginMode.Cross ? "cross" : "isolated",
            ["clOrdId"] = order.ClientId,
            ["side"] = order.Side == OrderSide.Buy ? "buy" : "sell",
            // OKX has no separate "how long": it is part of the order type.
            ["ordType"] = order.TimeInForce switch
            {
                TimeInForce.ImmediateOrCancel => "ioc",
                TimeInForce.PostOnly => "post_only",
                _ => "limit",
            },
            ["px"] = Plain(price),
            ["sz"] = Plain(size),
        };
        if (!spot)
        {
            if (order.ReduceOnly)
            {
                body["reduceOnly"] = true;
            }
            else
            {
                await SetLeverageAsync(credentials, instrument.Symbol, order, cancellationToken);
            }
        }

        JsonElement data = await SendAsync(credentials, HttpMethod.Post, "/api/v5/trade/order", body, true, cancellationToken);
        return data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? Text(data[0], "ordId") : null;
    }

    /// <summary>
    /// Leverage is ours to set. OKX will not change it while orders are working, so what it is now is asked first
    /// and it is only set when it differs. Nothing has been sent if this is refused.
    /// </summary>
    private async Task SetLeverageAsync(VenueCredentials credentials, string symbol, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        int leverage = (int)Math.Clamp(Math.Floor(order.Leverage), 1m, 125m);
        if (_set.TryGetValue((credentials.ApiKey, symbol), out (int Leverage, MarginMode Mode) set) && set == (leverage, order.MarginMode))
        {
            return;
        }

        string mode = order.MarginMode == MarginMode.Cross ? "cross" : "isolated";
        bool already = false;
        try
        {
            JsonElement now = await SendAsync(credentials, HttpMethod.Get, $"/api/v5/account/leverage-info?instId={symbol}&mgnMode={mode}", null, false, cancellationToken);
            already = now.ValueKind == JsonValueKind.Array && now.GetArrayLength() > 0 && now.EnumerateArray().All(l => Number(l, "lever") == leverage);
        }
        catch (VenueRefusedException)
        {
            // Not being able to read it is no reason not to set it.
        }

        if (!already)
        {
            await SendAsync(credentials, HttpMethod.Post, "/api/v5/account/set-leverage", new Dictionary<string, object> { ["instId"] = symbol, ["lever"] = leverage.ToString(CultureInfo.InvariantCulture), ["mgnMode"] = mode }, false, cancellationToken);
        }

        _set[(credentials.ApiKey, symbol)] = (leverage, order.MarginMode);
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken) =>
        await SendAsync(credentials, HttpMethod.Post, "/api/v5/trade/cancel-order", new Dictionary<string, object> { ["instId"] = instrument.Symbol, ["clOrdId"] = order.ClientId }, true, cancellationToken);

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        JsonElement data;
        try
        {
            data = await SendAsync(credentials, HttpMethod.Get, $"/api/v5/trade/order?instId={instrument.Symbol}&clOrdId={order.ClientId}", null, false, cancellationToken);
        }
        catch (VenueRefusedException e) when (e.Message.Contains("(code 51603)", StringComparison.Ordinal))
        {
            // "Order does not exist."
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        JsonElement o = data[0];
        decimal filled = Number(o, "accFillSz") * ContractSize(instrument);
        decimal average = Number(o, "avgPx");
        // The fee is negative when it was charged. On spot it may be in the coin that was bought.
        decimal? fees = filled > 0m ? // OKX writes a fee it charged as a negative number and a rebate as a positive one.
            FeesInDollars([(Number(o, "fee"), Text(o, "feeCcy"))], instrument.Symbol[..instrument.Symbol.IndexOf('-', StringComparison.Ordinal)], average, chargesAreNegative: true) : null;
        return new VenueOrderReport(Final.Contains(Text(o, "state") ?? string.Empty) ? VenueOrderState.Done : VenueOrderState.Open, filled, filled * average, fees, Text(o, "ordId"));
    }

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement balance = await SendAsync(credentials, HttpMethod.Get, "/api/v5/account/balance", null, false, cancellationToken);
        decimal cash = 0m;
        List<VenueHolding> holdings = [];
        if (balance.ValueKind == JsonValueKind.Array && balance.GetArrayLength() > 0 && balance[0].TryGetProperty("details", out JsonElement details))
        {
            foreach (JsonElement coin in details.EnumerateArray())
            {
                string name = (Text(coin, "ccy") ?? string.Empty).ToUpperInvariant();
                decimal equity = Number(coin, "eq");
                if (IsDollar(name))
                {
                    // One account for spot and swaps: a dollar coin's equity already includes the swaps' open profit and loss.
                    cash += equity;
                }
                else if (equity > 0m)
                {
                    holdings.Add(new VenueHolding(name, InstrumentType.Spot, equity, null, null, Number(coin, "eqUsd") is > 0m and decimal usd ? usd / equity : null));
                }
            }
        }

        JsonElement positions = await SendAsync(credentials, HttpMethod.Get, "/api/v5/account/positions?instType=SWAP", null, false, cancellationToken);
        if (positions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement position in positions.EnumerateArray())
            {
                string symbol = Text(position, "instId") ?? string.Empty;
                decimal contracts = Number(position, "pos");
                if (contracts == 0m || !symbol.EndsWith("-USDT-SWAP", StringComparison.Ordinal))
                {
                    continue;
                }

                string asset = symbol[..symbol.IndexOf('-', StringComparison.Ordinal)];
                if (!_contractSize.ContainsKey(symbol) && await InstrumentAsync(asset, InstrumentType.Perp, cancellationToken) is null)
                {
                    continue;
                }

                // In long/short mode the size is always positive and the side says which way it is.
                decimal signed = Text(position, "posSide") == "short" ? -Math.Abs(contracts) : contracts;
                decimal liquidation = Number(position, "liqPx");
                holdings.Add(new VenueHolding(asset, InstrumentType.Perp, signed * _contractSize[symbol], Number(position, "avgPx"), liquidation > 0m ? liquidation : null, Number(position, "markPx")));
            }
        }

        return new VenueAccountReport(cash, null, holdings);
    }

    /// <param name="perOrder">True for a request about one order: OKX then gives the order's own verdict beside the request's.</param>
    private async Task<JsonElement> SendAsync(VenueCredentials? credentials, HttpMethod method, string pathAndQuery, object? body, bool perOrder, CancellationToken cancellationToken)
    {
        string json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        using HttpRequestMessage request = new(method, Host + pathAndQuery);
        if (json.Length > 0)
        {
            request.Content = Body(json);
        }

        if (credentials is not null)
        {
            string timestamp = Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            request.Headers.Add("OK-ACCESS-KEY", credentials.ApiKey);
            request.Headers.Add("OK-ACCESS-SIGN", HmacBase64(credentials.ApiSecret, timestamp + method.Method.ToUpperInvariant() + pathAndQuery + json));
            request.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
            request.Headers.Add("OK-ACCESS-PASSPHRASE", credentials.Passphrase ?? string.Empty);
        }

        (HttpStatusCode status, string text) = await ExchangeAsync(request, cancellationToken);
        JsonElement? answer = Json(text);
        string code = answer is { ValueKind: JsonValueKind.Object } read ? Text(read, "code") ?? string.Empty : string.Empty;
        string message = answer is { ValueKind: JsonValueKind.Object } said ? Text(said, "msg") ?? string.Empty : string.Empty;
        JsonElement data = answer is { ValueKind: JsonValueKind.Object } with && with.TryGetProperty("data", out JsonElement d) ? d : default;
        // The verdict on the order itself, when there is one, is the one that counts.
        if (perOrder && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && Text(data[0], "sCode") is { Length: > 0 } own && own != "0")
        {
            code = own;
            message = Text(data[0], "sMsg") ?? message;
        }

        if (code == "0")
        {
            return data;
        }

        string reason = $"{Limited(message)} (code {(code.Length == 0 ? ((int)status).ToString(CultureInfo.InvariantCulture) : code)})";
        if (NotAnAnswer(status) || code.Length == 0 || NotAnswers.Contains(code))
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Execution;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Venues.Orders;

namespace KatalepsisFT.Venues.Deribit;

/// <summary>
/// Real orders at Deribit, for its perpetuals that are settled in USDC and sized in units of the asset
/// ("BTC_USDC-PERPETUAL"). Deribit has no leverage or margin mode to set per instrument: margin is the account's,
/// and each instrument has one net position. It gives no liquidation price for a position.
///
/// Deribit's name for an order of ours (its "label") is not unique there, so an order is asked about by Deribit's
/// own id as soon as that is known, and by label only for an order whose answer was lost.
/// </summary>
public sealed class DeribitOrderGateway(HttpClient http, TimeProvider clock) : RestOrderGateway(http, clock), IOrderGateway
{
    public const string HttpClientName = "deribit-orders";
    public const string Host = "https://www.deribit.com";
    private const string Suffix = "_USDC-PERPETUAL";

    /// <summary>
    /// Busy, paused, limited or failing inside. Some of these mean the order was certainly not taken, but treating
    /// them all as "it may have been" costs only a short wait, and the other mistake would cost an order we do not know of.
    /// </summary>
    private static readonly string[] NotAnswers = ["10001", "10028", "10040", "10041", "10047", "11051", "11094", "13028", "13503", "13888"];
    private static readonly string[] Final = ["filled", "cancelled", "rejected"];

    public Venue Venue => Venue.Deribit;

    public async Task<VenueInstrument?> InstrumentAsync(string asset, InstrumentType type, CancellationToken cancellationToken)
    {
        if (type != InstrumentType.Perp)
        {
            return null;
        }

        string name = asset.ToUpperInvariant() + Suffix;
        return await KeptAsync<VenueInstrument?>(name, async () =>
        {
            JsonElement listed;
            try
            {
                listed = await SendAsync(null, $"public/get_instrument?instrument_name={name}", cancellationToken);
            }
            catch (VenueRefusedException)
            {
                return null;
            }

            decimal step = Number(listed, "min_trade_amount");
            decimal tick = Number(listed, "tick_size");
            // Deribit can make the price step larger above certain prices. Where it does, the largest step is used:
            // a price on the coarsest grid is on every finer one too.
            if (listed.TryGetProperty("tick_size_steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement above in steps.EnumerateArray())
                {
                    tick = Math.Max(tick, Number(above, "tick_size"));
                }
            }

            return Text(listed, "state") != "open" || Text(listed, "instrument_type") != "linear" || step <= 0m || tick <= 0m ? null : new VenueInstrument(name, step, tick, step, 0m);
        });
    }

    public async Task<string?> PlaceAsync(VenueCredentials credentials, VenueInstrument instrument, VenueOrderRequest order, CancellationToken cancellationToken)
    {
        if (order.LimitPrice is not decimal price)
        {
            throw new VenueRefusedException("An order is only sent with a price.");
        }

        List<string> fields =
        [
            $"instrument_name={instrument.Symbol}",
            $"amount={Plain(order.Quantity)}",
            "type=limit",
            $"price={Plain(price)}",
            $"label={order.ClientId}",
            $"time_in_force={(order.TimeInForce == TimeInForce.ImmediateOrCancel ? "immediate_or_cancel" : "good_til_cancelled")}",
        ];
        if (order.TimeInForce == TimeInForce.PostOnly)
        {
            // Without the second flag Deribit would quietly move the price instead of refusing the order.
            fields.Add("post_only=true");
            fields.Add("reject_post_only=true");
        }

        if (order.ReduceOnly)
        {
            fields.Add("reduce_only=true");
        }

        // The side is in the name of the call.
        JsonElement result = await SendAsync(credentials, $"private/{(order.Side == OrderSide.Buy ? "buy" : "sell")}?{string.Join('&', fields)}", cancellationToken);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("order", out JsonElement placed) ? Text(placed, "order_id") : null;
    }

    public async Task CancelAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken) =>
        await SendAsync(credentials, order.VenueId is { } id ? $"private/cancel?order_id={Uri.EscapeDataString(id)}" : $"private/cancel_by_label?label={order.ClientId}&currency=USDC", cancellationToken);

    public async Task<VenueOrderReport> FindAsync(VenueCredentials credentials, VenueInstrument instrument, InstrumentType type, OrderNames order, CancellationToken cancellationToken)
    {
        JsonElement? found = null;
        if (order.VenueId is { } id)
        {
            try
            {
                JsonElement one = await SendAsync(credentials, $"private/get_order_state?order_id={Uri.EscapeDataString(id)}", cancellationToken);
                found = one.ValueKind == JsonValueKind.Object ? one : null;
            }
            catch (VenueRefusedException e) when (e.Message.Contains("(code 10004)", StringComparison.Ordinal))
            {
                // "order_not_found"
            }
        }
        else
        {
            JsonElement labelled = await SendAsync(credentials, $"private/get_order_state_by_label?currency=USDC&label={order.ClientId}", cancellationToken);
            if (labelled.ValueKind == JsonValueKind.Array)
            {
                // A label is not unique at Deribit; ours are, so more than one would be a surprise. The newest is taken.
                found = labelled.EnumerateArray().Where(o => Text(o, "label") == order.ClientId).OrderByDescending(o => Number(o, "creation_timestamp")).Cast<JsonElement?>().FirstOrDefault();
            }
        }

        if (found is null)
        {
            // Deribit forgets a closed order after thirty minutes; from then on it is only in the history. Without this
            // an order that filled while nobody was looking would be taken for one that was never there.
            JsonElement history = await SendAsync(credentials, $"private/get_order_history_by_instrument?instrument_name={Uri.EscapeDataString(instrument.Symbol)}&count=200&include_old=true&include_unfilled=true&historical=true", cancellationToken);
            if (history.ValueKind == JsonValueKind.Array)
            {
                found = history.EnumerateArray().Where(past => order.VenueId is { } wanted ? Text(past, "order_id") == wanted : Text(past, "label") == order.ClientId).OrderByDescending(past => Number(past, "creation_timestamp")).Cast<JsonElement?>().FirstOrDefault();
            }
        }

        if (found is not { } o)
        {
            return new VenueOrderReport(VenueOrderState.NotFound, 0m, 0m, null, null);
        }

        string? orderId = Text(o, "order_id");
        bool done = Final.Contains(Text(o, "order_state") ?? string.Empty);
        decimal filled = Number(o, "filled_amount");
        decimal average = Number(o, "average_price");
        decimal? fees = null;
        if (done && filled > 0m && orderId is not null)
        {
            fees = await FeesAsync(credentials, orderId, instrument.Symbol[..^Suffix.Length], average, cancellationToken);
        }

        return new VenueOrderReport(done ? VenueOrderState.Done : VenueOrderState.Open, filled, filled * average, fees, orderId, Text(o, "cancel_reason") is { Length: > 0 } why && why != "user_request" ? why.Replace('_', ' ') : null);
    }

    /// <summary>The order itself carries no fee; its trades do.</summary>
    private async Task<decimal?> FeesAsync(VenueCredentials credentials, string orderId, string asset, decimal price, CancellationToken cancellationToken)
    {
        try
        {
            JsonElement trades = await SendAsync(credentials, $"private/get_user_trades_by_order?order_id={Uri.EscapeDataString(orderId)}", cancellationToken);
            JsonElement list = trades.ValueKind == JsonValueKind.Object && trades.TryGetProperty("trades", out JsonElement inner) ? inner : trades;
            return list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0 ? null : FeesInDollars(list.EnumerateArray().Select(t => (Number(t, "fee"), Text(t, "fee_currency"))), asset, price);
        }
        catch (Exception e) when (e is VenueRefusedException or VenueUnclearException)
        {
            return null;
        }
    }

    public async Task<VenueAccountReport> AccountAsync(VenueCredentials credentials, CancellationToken cancellationToken)
    {
        JsonElement summary = await SendAsync(credentials, "private/get_account_summary?currency=USDC", cancellationToken);
        JsonElement positions = await SendAsync(credentials, "private/get_positions?currency=USDC&kind=future", cancellationToken);
        List<VenueHolding> holdings = [];
        if (positions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement position in positions.EnumerateArray())
            {
                string name = Text(position, "instrument_name") ?? string.Empty;
                // The size in units of the asset where Deribit gives it; the direction, not the sign, says which way.
                decimal size = Math.Abs(position.TryGetProperty("size_currency", out JsonElement inAsset) && inAsset.ValueKind == JsonValueKind.Number ? inAsset.GetDecimal() : Number(position, "size"));
                if (size > 0m && name.EndsWith(Suffix, StringComparison.Ordinal))
                {
                    holdings.Add(new VenueHolding(name[..^Suffix.Length], InstrumentType.Perp, Text(position, "direction") == "sell" ? -size : size, Number(position, "average_price"), null, Number(position, "mark_price")));
                }
            }
        }

        return new VenueAccountReport(0m, Number(summary, "equity"), holdings);
    }

    /// <summary>One call. Deribit takes its calls as plain addresses, with the key and secret in the standard sign-in header.</summary>
    private async Task<JsonElement> SendAsync(VenueCredentials? credentials, string call, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{Host}/api/v2/{call}");
        if (credentials is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.ApiKey}:{credentials.ApiSecret}")));
        }

        (HttpStatusCode status, string text) = await ExchangeAsync(request, cancellationToken);
        JsonElement? answer = Json(text);
        if (answer is { ValueKind: JsonValueKind.Object } good && good.TryGetProperty("result", out JsonElement result) && !good.TryGetProperty("error", out _))
        {
            return result;
        }

        JsonElement error = answer is { ValueKind: JsonValueKind.Object } failed && failed.TryGetProperty("error", out JsonElement e) ? e : default;
        string code = Text(error, "code") ?? string.Empty;
        string? detail = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out JsonElement data) ? Text(data, "reason") : null;
        string reason = $"{Limited(Text(error, "message"))}{(detail is null ? string.Empty : ": " + detail)} (code {(code.Length == 0 ? ((int)status).ToString(CultureInfo.InvariantCulture) : code)})";
        if (NotAnAnswer(status) || code.Length == 0 || NotAnswers.Contains(code))
        {
            throw new VenueUnclearException(reason);
        }

        throw new VenueRefusedException(reason);
    }
}

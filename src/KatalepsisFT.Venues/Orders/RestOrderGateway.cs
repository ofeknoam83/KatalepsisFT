using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Application.Execution;

namespace KatalepsisFT.Venues.Orders;

/// <summary>
/// What every venue's order gateway needs: one request and its answer, with a connection that fails or an answer that
/// does not come in time turned into "unclear"; the small things for reading and writing numbers; and a place to keep
/// what is asked for once and used many times, such as a venue's trading rules.
/// </summary>
public abstract class RestOrderGateway(HttpClient http, TimeProvider clock)
{
    protected static readonly TimeSpan RulesLife = TimeSpan.FromMinutes(10);
    private static readonly string[] Dollars = ["USDT", "USDC", "USD", "FDUSD", "USDE", "DAI", "TUSD"];
    private readonly ConcurrentDictionary<string, (object? Value, DateTimeOffset At)> _kept = new();

    protected TimeProvider Clock => clock;

    protected long NowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Sends one request. Throws <see cref="VenueUnclearException"/> when no answer came back.</summary>
    protected async Task<(HttpStatusCode Status, string Text)> ExchangeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new VenueUnclearException(e is OperationCanceledException ? "no answer in time" : "the connection failed", e);
        }
    }

    /// <summary>An answer as JSON, or nothing when it is not JSON (a proxy's error page, an empty body).</summary>
    protected static JsonElement? Json(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True for an HTTP status that says nothing about the request itself: the venue is busy, limiting or broken.</summary>
    protected static bool NotAnAnswer(HttpStatusCode status) => (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status == 418;

    /// <summary>Something asked for once and kept for a while. A failure to load is not kept.</summary>
    protected async Task<T> KeptAsync<T>(string name, Func<Task<T>> load)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (_kept.TryGetValue(name, out (object? Value, DateTimeOffset At) known) && now - known.At < RulesLife)
        {
            return (T)known.Value!;
        }

        T loaded = await load();
        _kept[name] = (loaded, now);
        return loaded;
    }

    protected static bool IsDollar(string? currency) => currency is not null && Dollars.Contains(currency, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fees in dollars from fees in whatever each was charged in. A dollar currency counts as it is; the traded asset
    /// itself is taken at the order's own price; anything else (a venue's own token) cannot be told here, and then
    /// there is no figure at all.
    /// </summary>
    protected static decimal? FeesInDollars(IEnumerable<(decimal Amount, string? Currency)> fees, string asset, decimal price)
    {
        decimal total = 0m;
        foreach ((decimal amount, string? currency) in fees)
        {
            if (amount == 0m)
            {
                continue;
            }

            if (IsDollar(currency))
            {
                total += Math.Abs(amount);
            }
            else if (string.Equals(currency, asset, StringComparison.OrdinalIgnoreCase) && price > 0m)
            {
                total += Math.Abs(amount) * price;
            }
            else
            {
                return null;
            }
        }

        return total;
    }

    protected static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>A number that may come as a number or as text; nothing, an empty text or something else is zero.</summary>
    protected static decimal Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) ? Number(value) : 0m;

    protected static decimal Number(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) => parsed,
            _ => 0m,
        };

    /// <summary>A number as venues want it: plain digits, no exponent, no trailing zeros.</summary>
    protected static string Plain(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    protected static string HmacHex(string secret, string text) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    protected static string HmacBase64(string secret, string text) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text)));

    protected static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    protected static string Limited(string? text) => string.IsNullOrWhiteSpace(text) ? "no reason given" : text.Length <= 300 ? text : text[..300];
}

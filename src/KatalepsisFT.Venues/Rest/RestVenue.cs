using System.Globalization;
using System.Net;
using System.Text.Json;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Venues.Rest;

/// <summary>
/// What the hand-written exchange adapters share: sending a request, reading the answer as JSON, and turning any
/// failure into a <see cref="VenueException"/> whose text is the venue's own words, never anything that was sent.
/// </summary>
internal static class RestVenue
{
    public static async Task<(HttpStatusCode Status, JsonElement? Body, string Raw)> SendAsync(HttpClient http, HttpRequestMessage request, string venue, string what, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "KatalepsisFT");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        HttpResponseMessage response;
        string raw;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
            raw = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new VenueException(VenueFailure.Unavailable, $"{venue} did not answer {what}.", e);
        }

        using (response)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                return (response.StatusCode, document.RootElement.Clone(), raw);
            }
            catch (JsonException)
            {
                if (response.IsSuccessStatusCode)
                {
                    throw new VenueException(VenueFailure.Unavailable, $"{venue} answered {what} with something unreadable.");
                }

                return (response.StatusCode, null, raw);
            }
        }
    }

    public static string Text(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out JsonElement value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.GetRawText() : string.Empty
            : string.Empty;

    /// <summary>These venues write numbers as strings or as numbers, depending on the field.</summary>
    public static decimal? Number(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) => parsed,
            _ => null,
        };
    }

    public static string Short(string text) => text.Length <= 200 ? text : text[..200];

    /// <summary>Holdings per asset as cash and spot lines; a coin without a USD price fails the read rather than vanish from NAV.</summary>
    public static List<VenuePosition> Holdings(string venue, IReadOnlyDictionary<string, decimal> holdings, Func<string, decimal?> usdPrice)
    {
        List<VenuePosition> result = [];
        foreach ((string asset, decimal quantity) in holdings.Where(h => h.Value != 0m).OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            string symbol = asset.ToUpperInvariant();
            if (VenueText.IsCash(symbol))
            {
                result.Add(new VenuePosition(InstrumentType.Cash, symbol, quantity, 1m));
            }
            else
            {
                result.Add(new VenuePosition(InstrumentType.Spot, symbol, quantity, usdPrice(symbol) is decimal price and > 0m ? price : throw new VenueException(VenueFailure.Unavailable, $"{venue} holds {symbol} but lists no USD price for it.")));
            }
        }

        return result;
    }

    public static void Add(Dictionary<string, decimal> holdings, string asset, decimal quantity)
    {
        if (asset.Length > 0 && quantity != 0m)
        {
            holdings[asset] = holdings.GetValueOrDefault(asset) + quantity;
        }
    }
}

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KatalepsisFT.Application.Connections;

namespace KatalepsisFT.Venues.Coinbase;

/// <summary>
/// Signed GET requests to Coinbase's two institutional APIs. The signing is that of Katalepsis'
/// Infrastructure/Services/Coinbase/CoinabseHelper.cs (commit 9847e4f): HMAC-SHA256 over timestamp, method, path and
/// body, keyed with the base64-decoded secret on Exchange and with the secret's own bytes on Prime. Only GET is offered.
/// </summary>
internal sealed class CoinbaseHttp(HttpClient http, TimeProvider clock, string venue, string baseUrl, bool prime)
{
    public async Task<JsonElement> GetAsync(VenueCredentials credentials, string path, string? query, CancellationToken cancellationToken)
    {
        string timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        // Prime signs the path alone; Exchange signs it with its query string.
        string signedPath = prime || query is null ? path : $"{path}?{query}";
        string signature;
        try
        {
            byte[] key = prime ? Encoding.UTF8.GetBytes(credentials.ApiSecret) : Convert.FromBase64String(credentials.ApiSecret);
            signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(timestamp + "GET" + signedPath)));
        }
        catch (FormatException)
        {
            throw new VenueException(VenueFailure.Rejected, "the API secret is not in the form this venue issues.");
        }

        string prefix = prime ? "X-CB-ACCESS-" : "CB-ACCESS-";
        using HttpRequestMessage request = new(HttpMethod.Get, baseUrl + path + (query is null ? string.Empty : "?" + query));
        request.Headers.TryAddWithoutValidation(prefix + "KEY", credentials.ApiKey);
        request.Headers.TryAddWithoutValidation(prefix + "PASSPHRASE", credentials.Passphrase ?? string.Empty);
        request.Headers.TryAddWithoutValidation(prefix + (prime ? "SIGNATURE" : "SIGN"), signature);
        request.Headers.TryAddWithoutValidation(prefix + "TIMESTAMP", timestamp);
        return await SendAsync(request, path, cancellationToken);
    }

    public async Task<JsonElement> GetPublicAsync(string url, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        return await SendAsync(request, new Uri(url).AbsolutePath, cancellationToken);
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        // Coinbase refuses requests that do not say what is calling.
        request.Headers.TryAddWithoutValidation("User-Agent", "KatalepsisFT");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        HttpResponseMessage response;
        string body;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new VenueException(VenueFailure.Unavailable, $"{venue} did not answer {what}.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                bool rejected = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                throw new VenueException(rejected ? VenueFailure.Rejected : VenueFailure.Unavailable, $"{Message(body) ?? "HTTP " + (int)response.StatusCode} on {what}.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                return document.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new VenueException(VenueFailure.Unavailable, $"{venue} answered {what} with something unreadable.", e);
            }
        }
    }

    /// <summary>The venue's own words for an error, shortened; never anything we sent.</summary>
    private static string? Message(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
            {
                string text = message.GetString() ?? string.Empty;
                return text.Length <= 200 ? text : text[..200];
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the status code.
        }

        return null;
    }

    public static string Text(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>Coinbase writes numbers as strings.</summary>
    public static decimal? Number(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value))
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

    /// <summary>"2026-11-21T08:00:00Z" on Prime; "2026-11-21 08:00:00.000000+00" on Exchange.</summary>
    public static DateTimeOffset? Time(JsonElement parent, string property)
    {
        string text = Text(parent, property);
        if (text.Length == 0)
        {
            return null;
        }

        string normal = text.Replace(' ', 'T');
        if (normal.Length > 3 && normal[^3] is '+' or '-')
        {
            normal += ":00";
        }

        return DateTimeOffset.TryParse(normal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time) ? time : null;
    }
}

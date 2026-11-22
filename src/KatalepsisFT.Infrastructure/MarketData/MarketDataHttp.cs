// Copied from Katalepsis (Horizen.Ria/Integration/MarketData/MarketDataHttp.cs, commit 9847e4f).
// Changed: the namespace and the names of the HTTP clients.
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace KatalepsisFT.Infrastructure.MarketData;

/// <summary>Thrown when data a definition cannot do without (e.g. the price of a weighted asset) cannot be fetched.</summary>
public sealed class MarketDataUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Named HttpClients of the public market-data APIs (no credentials; read-only).</summary>
public static class MarketDataHttpClients
{
    public const string Binance = "KatalepsisFT.Binance";
    public const string BinanceFutures = "KatalepsisFT.BinanceFutures";
    public const string Deribit = "KatalepsisFT.Deribit";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
}

/// <summary>Result of a public GET: the JSON body, or the status and body of an error.</summary>
internal sealed class ApiResponse(HttpStatusCode status, string body)
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

internal static class PublicApi
{
    /// <summary>Default cache lifetime of live data.</summary>
    public static readonly TimeSpan LiveTtl = TimeSpan.FromMinutes(5);

    /// <summary>Cache lifetime of historical data (closed days do not change).</summary>
    public static readonly TimeSpan HistoryTtl = TimeSpan.FromMinutes(30);

    public static async Task<ApiResponse> GetAsync(IHttpClientFactory factory, string clientName, string url, CancellationToken cancellationToken)
    {
        HttpClient client = factory.CreateClient(clientName);
        try
        {
            using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ApiResponse(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new MarketDataUnavailableException($"{Host(url)} did not answer ({e.Message}).", e);
        }
    }

    public static async Task<T> CachedAsync<T>(IMemoryCache cache, string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        if (cache.TryGetValue(key, out object? cached) && cached is T value)
        {
            return value;
        }

        T result = await factory();
        if (result is not null)
        {
            cache.Set(key, result, ttl);
        }

        return result;
    }

    public static decimal ParseDecimal(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.String => decimal.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => 0m,
        };

    public static decimal? ParseNullableDecimal(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? ParseDecimal(value)
            : null;

    public static DateTime FromUnixMs(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;

    public static long ToUnixMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : url;

    public static string Trim(string body) => body.Length <= 200 ? body : body[..200];
}

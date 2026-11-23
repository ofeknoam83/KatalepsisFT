using System.Globalization;
using System.Net;
using System.Text.Json;
using KatalepsisFT.Application.Prices;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Infrastructure.MarketData;

/// <summary>
/// Daily closes and traded volume of listed shares from Yahoo Finance's public chart data: free and without a key,
/// but not an official, supported service, so it can change or stop without notice. Prices are in the listing's own
/// currency and are treated as USD, which is right for US listings only.
/// </summary>
public sealed class YahooDailyPriceSource(IHttpClientFactory httpClientFactory) : IDailyPriceSource
{
    public const string HttpClientName = "KatalepsisFT.Yahoo";
    public const string BaseUrl = "https://query1.finance.yahoo.com/v8/finance/chart/";

    public string Name => "yahoo";

    public AssetClass AssetClass => AssetClass.Equity;

    public async Task<IReadOnlyList<DailyBar>?> GetDailyBarsAsync(string assetSymbol, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        long start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        // Up to the end of the last day asked for, so a day still trading is never included.
        long end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        // Yahoo writes share classes with a dash: BRK.B is BRK-B.
        string symbol = Uri.EscapeDataString(assetSymbol.ToUpperInvariant().Replace('.', '-'));
        string url = $"{BaseUrl}{symbol}?period1={start.ToString(CultureInfo.InvariantCulture)}&period2={end.ToString(CultureInfo.InvariantCulture)}&interval=1d";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        // Requests without a browser-like agent are turned away.
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; KatalepsisFT)");
        HttpResponseMessage response;
        string body;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new MarketDataUnavailableException($"Yahoo Finance did not answer ({e.Message}).", e);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MarketDataUnavailableException($"Yahoo Finance answered {(int)response.StatusCode} for {assetSymbol}.");
            }

            return Parse(body, from, to);
        }
    }

    /// <summary>The bars in Yahoo's answer; null when it says it does not know the symbol. Days without a close (halts) are left out.</summary>
    public static IReadOnlyList<DailyBar>? Parse(string json, DateOnly from, DateOnly to)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement chart = document.RootElement.GetProperty("chart");
        if (!chart.TryGetProperty("result", out JsonElement results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement result = results[0];
        if (!result.TryGetProperty("timestamp", out JsonElement timestamps))
        {
            // A known symbol with no trading days in the period.
            return [];
        }

        JsonElement quote = result.GetProperty("indicators").GetProperty("quote")[0];
        JsonElement closes = quote.GetProperty("close");
        JsonElement volumes = quote.GetProperty("volume");
        List<DailyBar> bars = [];
        for (int i = 0; i < timestamps.GetArrayLength(); i++)
        {
            if (closes[i].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            DateOnly day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime);
            decimal close = Math.Round((decimal)closes[i].GetDouble(), 8);
            decimal shares = volumes[i].ValueKind == JsonValueKind.Number ? volumes[i].GetDecimal() : 0m;
            if (day >= from && day <= to && close > 0m)
            {
                bars.Add(new DailyBar(day, close, Math.Round(shares * close, 2)));
            }
        }

        return bars;
    }
}

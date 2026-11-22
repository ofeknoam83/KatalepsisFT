// Copied from Katalepsis (Horizen.Ria/Integration/MarketData/BinancePublicClient.cs, commit 9847e4f).
// Changed: the namespace; the spot base URL, now Binance's public market-data host, which serves the same paths
// without regional restrictions; cache key prefix. Added: the traded volume of each day (QuoteVolumes).
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace KatalepsisFT.Infrastructure.MarketData;

/// <summary>Daily USDT closes of one asset: completed days only, plus the latest (live) price.</summary>
public sealed record DailySeries(string Asset, IReadOnlyList<DateTime> Days, IReadOnlyList<decimal> Closes, decimal LatestPrice)
{
    /// <summary>Traded volume in USDT per day, aligned with <see cref="Closes"/>.</summary>
    public IReadOnlyList<decimal> QuoteVolumes { get; init; } = [];
}

/// <summary>Binance public REST (spot klines, USDⓈ-M funding). No keys; USDT pairs are treated as USD.</summary>
public sealed class BinancePublicClient(IHttpClientFactory httpClientFactory, IMemoryCache cache, TimeProvider? timeProvider = null)
{
    public const string SpotBaseUrl = "https://data-api.binance.vision";
    public const string FuturesBaseUrl = "https://fapi.binance.com";
    public const string Quote = "USDT";
    private const int MaxLimit = 1000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public static string Symbol(string asset) => asset.ToUpperInvariant() + Quote;

    /// <summary>The last <paramref name="days"/> completed daily closes and the current price; null when Binance does not list the pair.</summary>
    public Task<DailySeries?> GetRecentDailyAsync(string asset, int days, CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(days + 1, 2, MaxLimit);
        string url = $"{SpotBaseUrl}/api/v3/klines?symbol={Symbol(asset)}&interval=1d&limit={limit}";
        return PublicApi.CachedAsync(
            cache,
            $"kft:binance:recent:{Symbol(asset)}:{limit}",
            PublicApi.LiveTtl,
            async () =>
            {
                List<Kline>? klines = await GetKlinesAsync(url, cancellationToken);
                if (klines is null || klines.Count == 0)
                {
                    return null;
                }

                DateTime now = _time.GetUtcNow().UtcDateTime;
                List<Kline> completed = klines.Where(k => k.CloseTime < now).ToList();
                return new DailySeries(
                    asset.ToUpperInvariant(),
                    completed.Select(k => k.OpenTime.Date).ToList(),
                    completed.Select(k => k.Close).ToList(),
                    klines[^1].Close
                )
                {
                    QuoteVolumes = completed.Select(k => k.QuoteVolume).ToList(),
                };
            }
        );
    }

    /// <summary>Completed daily closes between two UTC days (inclusive), paged; null when the pair is not listed.</summary>
    public Task<DailySeries?> GetDailyRangeAsync(string asset, DateTime startDay, DateTime endDay, CancellationToken cancellationToken) =>
        PublicApi.CachedAsync(
            cache,
            $"kft:binance:range:{Symbol(asset)}:{startDay:yyyyMMdd}:{endDay:yyyyMMdd}",
            PublicApi.HistoryTtl,
            async () =>
            {
                DateTime now = _time.GetUtcNow().UtcDateTime;
                long endMs = PublicApi.ToUnixMs(endDay.Date.AddDays(1)) - 1;
                long cursor = PublicApi.ToUnixMs(startDay.Date);
                var all = new List<Kline>();
                for (int page = 0; page < 100 && cursor <= endMs; page++)
                {
                    string url =
                        $"{SpotBaseUrl}/api/v3/klines?symbol={Symbol(asset)}&interval=1d&startTime={cursor}&endTime={endMs}&limit={MaxLimit}";
                    List<Kline>? klines = await GetKlinesAsync(url, cancellationToken);
                    if (klines is null)
                    {
                        return null;
                    }

                    all.AddRange(klines);
                    if (klines.Count < MaxLimit)
                    {
                        break;
                    }

                    cursor = PublicApi.ToUnixMs(klines[^1].OpenTime) + 1;
                }

                List<Kline> completed = all.Where(k => k.CloseTime < now).DistinctBy(k => k.OpenTime).OrderBy(k => k.OpenTime).ToList();
                return new DailySeries(
                    asset.ToUpperInvariant(),
                    completed.Select(k => k.OpenTime.Date).ToList(),
                    completed.Select(k => k.Close).ToList(),
                    completed.Count > 0 ? completed[^1].Close : 0m
                )
                {
                    QuoteVolumes = completed.Select(k => k.QuoteVolume).ToList(),
                };
            }
        );

    /// <summary>Funding events (time, rate per period) between two instants, paged; null when no perp is listed.</summary>
    public Task<IReadOnlyList<(DateTime Time, decimal Rate)>?> GetFundingAsync(
        string asset,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken
    ) =>
        PublicApi.CachedAsync<IReadOnlyList<(DateTime Time, decimal Rate)>?>(
            cache,
            $"kft:binance:funding:{Symbol(asset)}:{start:yyyyMMddHH}:{end:yyyyMMddHH}",
            end.Date < _time.GetUtcNow().UtcDateTime.Date ? PublicApi.HistoryTtl : PublicApi.LiveTtl,
            async () =>
            {
                long endMs = PublicApi.ToUnixMs(end);
                long cursor = PublicApi.ToUnixMs(start);
                var events = new List<(DateTime Time, decimal Rate)>();
                for (int page = 0; page < 100 && cursor <= endMs; page++)
                {
                    string url =
                        $"{FuturesBaseUrl}/fapi/v1/fundingRate?symbol={Symbol(asset)}&startTime={cursor}&endTime={endMs}&limit={MaxLimit}";
                    ApiResponse response = await PublicApi.GetAsync(httpClientFactory, MarketDataHttpClients.BinanceFutures, url, cancellationToken);
                    if (IsUnknownSymbol(response))
                    {
                        return null;
                    }

                    EnsureSuccess(response, url);
                    using JsonDocument document = JsonDocument.Parse(response.Body);
                    int count = 0;
                    long last = cursor;
                    foreach (JsonElement item in document.RootElement.EnumerateArray())
                    {
                        long time = item.GetProperty("fundingTime").GetInt64();
                        events.Add((PublicApi.FromUnixMs(time), PublicApi.ParseDecimal(item.GetProperty("fundingRate"))));
                        last = Math.Max(last, time);
                        count++;
                    }

                    if (count < MaxLimit)
                    {
                        break;
                    }

                    cursor = last + 1;
                }

                return events.DistinctBy(e => e.Time).OrderBy(e => e.Time).ToList();
            }
        );

    /// <summary>Mean funding rate per period annualised with the observed funding interval (8h when unknown).</summary>
    public static decimal? Annualise(IReadOnlyList<(DateTime Time, decimal Rate)> events)
    {
        if (events.Count == 0)
        {
            return null;
        }

        double intervalHours = 8d;
        if (events.Count >= 2)
        {
            List<double> gaps = events.Zip(events.Skip(1), (a, b) => (b.Time - a.Time).TotalHours).Where(h => h > 0d).OrderBy(h => h).ToList();
            if (gaps.Count > 0)
            {
                intervalHours = gaps[gaps.Count / 2];
            }
        }

        decimal periodsPerYear = (decimal)(365d * 24d / intervalHours);
        return events.Average(e => e.Rate) * periodsPerYear;
    }

    private async Task<List<Kline>?> GetKlinesAsync(string url, CancellationToken cancellationToken)
    {
        ApiResponse response = await PublicApi.GetAsync(httpClientFactory, MarketDataHttpClients.Binance, url, cancellationToken);
        if (IsUnknownSymbol(response))
        {
            return null;
        }

        EnsureSuccess(response, url);
        using JsonDocument document = JsonDocument.Parse(response.Body);
        var klines = new List<Kline>();
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            klines.Add(
                new Kline(
                    PublicApi.FromUnixMs(row[0].GetInt64()),
                    PublicApi.ParseDecimal(row[4]),
                    PublicApi.FromUnixMs(row[6].GetInt64()),
                    PublicApi.ParseDecimal(row[7])
                )
            );
        }

        return klines;
    }

    /// <summary>Binance answers 400 with code -1121 ("Invalid symbol.") for pairs it does not list.</summary>
    private static bool IsUnknownSymbol(ApiResponse response) =>
        response.Status == System.Net.HttpStatusCode.BadRequest && response.Body.Contains("-1121", StringComparison.Ordinal);

    private static void EnsureSuccess(ApiResponse response, string url)
    {
        if (!response.IsSuccess)
        {
            throw new MarketDataUnavailableException(
                $"Binance answered {(int)response.Status} for {new Uri(url).AbsolutePath}: {PublicApi.Trim(response.Body)}"
            );
        }
    }

    private sealed record Kline(DateTime OpenTime, decimal Close, DateTime CloseTime, decimal QuoteVolume);
}

using System.Globalization;
using System.Text.Json;
using KatalepsisFT.Application.Prices;

namespace KatalepsisFT.Infrastructure.MarketData;

/// <summary>Latest traded prices from Binance's public market-data host. No keys; USDT pairs are treated as USD.</summary>
public sealed class BinanceLivePriceSource(IHttpClientFactory httpClientFactory) : ILivePriceSource
{
    public async Task<IReadOnlyDictionary<string, decimal>> GetPricesAsync(IReadOnlyCollection<string> assetSymbols, CancellationToken cancellationToken)
    {
        Dictionary<string, decimal> prices = new(StringComparer.OrdinalIgnoreCase);
        // One call for every pair costs the same as a call for a few; unknown names in a filtered call would fail it whole.
        using HttpResponseMessage response = await httpClientFactory.CreateClient(MarketDataHttpClients.Binance).GetAsync($"{BinancePublicClient.SpotBaseUrl}/api/v3/ticker/price", cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        HashSet<string> wanted = assetSymbols.Select(BinancePublicClient.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement ticker in document.RootElement.EnumerateArray())
        {
            string symbol = ticker.GetProperty("symbol").GetString()!;
            if (wanted.Contains(symbol) && decimal.TryParse(ticker.GetProperty("price").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal price) && price > 0m)
            {
                prices[symbol[..^BinancePublicClient.Quote.Length]] = price;
            }
        }

        return prices;
    }
}

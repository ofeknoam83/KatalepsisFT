using KatalepsisFT.Application.Connections;
using KatalepsisFT.Venues.Binance;
using KatalepsisFT.Venues.Bybit;
using KatalepsisFT.Venues.Chains;
using KatalepsisFT.Venues.Coinbase;
using KatalepsisFT.Venues.Deribit;
using KatalepsisFT.Venues.Hyperliquid;
using KatalepsisFT.Venues.Kucoin;
using KatalepsisFT.Venues.Mexc;
using KatalepsisFT.Venues.Okx;
using KatalepsisFT.Venues.Rest;
using Microsoft.Extensions.DependencyInjection;

namespace KatalepsisFT.Venues;

public static class DependencyInjection
{
    /// <summary>One line per venue. Nothing else in the system switches on venue.</summary>
    public static IServiceCollection AddVenueAdapters(this IServiceCollection services)
    {
        services.AddHttpClient(DeribitAdapter.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IVenueAdapter>(provider => new DeribitAdapter(provider.GetRequiredService<IHttpClientFactory>().CreateClient(DeribitAdapter.HttpClientName), provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<IVenueAdapter, OkxAdapter>();
        services.AddScoped<IVenueAdapter, BinanceAdapter>();
        services.AddScoped<IVenueAdapter, BybitAdapter>();
        services.AddScoped<IVenueAdapter, KucoinAdapter>();
        services.AddScoped<IVenueAdapter, MexcAdapter>();
        services.AddHttpClient(CoinbaseExchangeAdapter.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IVenueAdapter>(provider => new CoinbaseExchangeAdapter(provider.GetRequiredService<IHttpClientFactory>().CreateClient(CoinbaseExchangeAdapter.HttpClientName), provider.GetRequiredService<TimeProvider>()));
        services.AddHttpClient(CoinbasePrimeAdapter.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IVenueAdapter>(provider => new CoinbasePrimeAdapter(provider.GetRequiredService<IHttpClientFactory>().CreateClient(CoinbasePrimeAdapter.HttpClientName), provider.GetRequiredService<TimeProvider>()));
        services.AddHttpClient(HyperliquidAdapter.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IVenueAdapter>(provider => new HyperliquidAdapter(provider.GetRequiredService<IHttpClientFactory>().CreateClient(HyperliquidAdapter.HttpClientName), provider.GetRequiredService<TimeProvider>()));
        foreach (string name in new[] { KrakenAdapter.HttpClientName, GateAdapter.HttpClientName, BitgetAdapter.HttpClientName })
        {
            services.AddHttpClient(name, client => client.Timeout = TimeSpan.FromSeconds(20));
        }

        services.AddScoped<IVenueAdapter>(provider => new KrakenAdapter(provider.GetRequiredService<IHttpClientFactory>().CreateClient(KrakenAdapter.HttpClientName), provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<IVenueAdapter>(provider => new GateAdapter(provider.GetRequiredService<IHttpClientFactory>().CreateClient(GateAdapter.HttpClientName), provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<IVenueAdapter>(provider => new BitgetAdapter(provider.GetRequiredService<IHttpClientFactory>().CreateClient(BitgetAdapter.HttpClientName), provider.GetRequiredService<TimeProvider>()));
        services.AddHttpClient(WalletAdapter.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IVenueAdapter>(provider => new EvmWalletAdapter(ChainClient(provider), provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<IVenueAdapter>(provider => new SolanaWalletAdapter(ChainClient(provider), provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<IVenueAdapter>(provider => new BitcoinWalletAdapter(ChainClient(provider), provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<IVenueAdapter>(provider => new TronWalletAdapter(ChainClient(provider), provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    private static HttpClient ChainClient(IServiceProvider provider) => provider.GetRequiredService<IHttpClientFactory>().CreateClient(WalletAdapter.HttpClientName);
}

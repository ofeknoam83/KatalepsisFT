using KatalepsisFT.Application.Connections;
using KatalepsisFT.Venues.Binance;
using KatalepsisFT.Venues.Bybit;
using KatalepsisFT.Venues.Deribit;
using KatalepsisFT.Venues.Okx;
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
        return services;
    }
}

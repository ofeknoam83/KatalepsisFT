using KatalepsisFT.Application.Prices;
using KatalepsisFT.Infrastructure.Health;
using KatalepsisFT.Infrastructure.MarketData;
using KatalepsisFT.Infrastructure.KeyValue;
using KatalepsisFT.Infrastructure.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;

namespace KatalepsisFT.Infrastructure;

public static class DependencyInjection
{
    public const string PostgresUrlKey = "DATABASE_URL";
    public const string KeyValueUrlKey = "KEYVALUE_URL";

    /// <summary>Registers Postgres and Key Value when their addresses are configured. Returns which of the two were registered.</summary>
    public static (bool Postgres, bool KeyValue) AddStores(this IServiceCollection services, IConfiguration configuration)
    {
        string? postgresUrl = configuration[PostgresUrlKey];
        if (!string.IsNullOrWhiteSpace(postgresUrl))
        {
            services.AddSingleton(NpgsqlDataSource.Create(PostgresConnection.FromUrl(postgresUrl)));
        }

        string? keyValueUrl = configuration[KeyValueUrlKey];
        if (!string.IsNullOrWhiteSpace(keyValueUrl))
        {
            ConfigurationOptions options = KeyValueConnection.Parse(keyValueUrl);
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        }

        return (!string.IsNullOrWhiteSpace(postgresUrl), !string.IsNullOrWhiteSpace(keyValueUrl));
    }

    /// <summary>Encryption of stored venue keys under the MASTER_KEY secret. Without the secret the vault reports itself unavailable.</summary>
    public static IServiceCollection AddCredentialVault(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<Application.Connections.ICredentialVault>(new Security.CredentialVault(configuration[Security.CredentialVault.MasterKeySetting]));
        return services;
    }

    /// <summary>
    /// What the web service needs to take orders in: a way to seal trading keys, the book the execution service
    /// publishes, and a way to wake it. Without a Key Value store there is no book and no wake-up call; orders are
    /// still written down and found by the execution service on its next round.
    /// </summary>
    public static IServiceCollection AddExecutionClient(this IServiceCollection services)
    {
        services.AddSingleton<Application.Execution.ITradingVault, Execution.SealedTradingVault>();
        services.AddSingleton<Application.Execution.IBookFeed>(provider => provider.GetService<IConnectionMultiplexer>() is { } keyValue ? new Execution.KeyValueBookFeed(keyValue) : new Execution.NoBookFeed());
        services.AddSingleton<Application.Execution.IExecutionSignal>(provider =>
            provider.GetService<IConnectionMultiplexer>() is { } keyValue ? new Execution.KeyValueExecutionSignal(keyValue, provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Execution.KeyValueExecutionSignal>>()) : new Execution.NoExecutionSignal()
        );
        return services;
    }

    /// <summary>The mail account reports are sent from; without one configured, nothing is sent and the screens say so.</summary>
    public static IServiceCollection AddMail(this IServiceCollection services)
    {
        services.AddSingleton<KatalepsisFT.Application.Reports.IMailSender, Mail.SmtpMailSender>();
        return services;
    }

    /// <summary>Public market data: no credentials, read-only.</summary>
    public static IServiceCollection AddMarketData(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddHttpClient(MarketDataHttpClients.Binance, client => client.Timeout = MarketDataHttpClients.Timeout);
        services.AddHttpClient(MarketDataHttpClients.BinanceFutures, client => client.Timeout = MarketDataHttpClients.Timeout);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BinancePublicClient>(provider => new BinancePublicClient(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            provider.GetRequiredService<TimeProvider>()
        ));
        services.AddSingleton<IDailyPriceSource, BinanceDailyPriceSource>();
        services.AddSingleton<ILivePriceSource, BinanceLivePriceSource>();
        services.AddHttpClient(YahooDailyPriceSource.HttpClientName, client => client.Timeout = MarketDataHttpClients.Timeout);
        services.AddSingleton<IDailyPriceSource, YahooDailyPriceSource>();
        return services;
    }
}

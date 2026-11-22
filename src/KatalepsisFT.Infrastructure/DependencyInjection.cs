using KatalepsisFT.Infrastructure.Health;
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
}

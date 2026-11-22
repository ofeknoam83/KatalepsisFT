using Npgsql;
using StackExchange.Redis;

namespace KatalepsisFT.Infrastructure.Health;

public sealed record ReadinessReport(string Postgres, string KeyValue, IReadOnlyDictionary<string, double?> WorkerHeartbeatAgeSeconds)
{
    public const string Ok = "ok";
    public const string NotConfigured = "not configured";

    /// <summary>Ready means every store that is configured answers. Missing worker heartbeats are reported but do not make the web service unready.</summary>
    public bool IsReady => Postgres is Ok or NotConfigured && KeyValue is Ok or NotConfigured;
}

/// <summary>Checks the stores the web service depends on. A store with no address configured is reported as such, not as failed.</summary>
public sealed class ReadinessProbe(HeartbeatRegistry heartbeats, NpgsqlDataSource? postgres = null, IConnectionMultiplexer? keyValue = null)
{
    public async Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken)
    {
        string postgresState = ReadinessReport.NotConfigured;
        if (postgres is not null)
        {
            try
            {
                await using NpgsqlCommand command = postgres.CreateCommand("select 1");
                await command.ExecuteScalarAsync(cancellationToken);
                postgresState = ReadinessReport.Ok;
            }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                postgresState = $"failed: {e.GetType().Name}";
            }
        }

        string keyValueState = ReadinessReport.NotConfigured;
        if (keyValue is not null)
        {
            try
            {
                await keyValue.GetDatabase().PingAsync();
                keyValueState = ReadinessReport.Ok;
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                keyValueState = $"failed: {e.GetType().Name}";
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyDictionary<string, DateTimeOffset> seen = heartbeats.Snapshot();
        Dictionary<string, double?> ages = WorkerGroups.All.ToDictionary(
            group => group,
            group => seen.TryGetValue(group, out DateTimeOffset at) ? Math.Round((now - at).TotalSeconds, 1) : (double?)null
        );

        return new ReadinessReport(postgresState, keyValueState, ages);
    }
}

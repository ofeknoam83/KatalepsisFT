using System.Collections.Concurrent;

namespace KatalepsisFT.Infrastructure.Health;

/// <summary>Last heartbeat received from each worker over the Key Value channel, held in memory by the web service.</summary>
public sealed class HeartbeatRegistry
{
    public const string Channel = "ft:heartbeat";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string group, DateTimeOffset at) => _lastSeen[group] = at;

    public IReadOnlyDictionary<string, DateTimeOffset> Snapshot() => new Dictionary<string, DateTimeOffset>(_lastSeen, StringComparer.OrdinalIgnoreCase);
}

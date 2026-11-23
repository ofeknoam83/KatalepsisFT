namespace KatalepsisFT.Infrastructure.Health;

/// <summary>The three always-on workers. Each Render worker service is started with one of these names.</summary>
public static class WorkerGroups
{
    public const string Cefi = "cefi";
    public const string Defi = "defi";
    public const string Core = "core";

    /// <summary>The execution service: market data streams, the order engine and the trading-key vault.</summary>
    public const string Exec = "exec";

    public static readonly IReadOnlyList<string> All = [Cefi, Defi, Core, Exec];
}

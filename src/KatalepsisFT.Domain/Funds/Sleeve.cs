namespace KatalepsisFT.Domain.Funds;

public enum MandateType
{
    Beta = 1,
    MarketNeutral = 2,
    Directional = 3,
    Other = 4,
}

/// <summary>A slice of a fund with one mandate, run by one or more managers.</summary>
public class Sleeve
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public Fund Fund { get; set; } = null!;
    public required string Name { get; set; }
    public MandateType MandateType { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Overrides the fund's benchmark for this sleeve when set.</summary>
    public int? BenchmarkId { get; set; }
    public Benchmarks.Benchmark? Benchmark { get; set; }
    public List<ManagerAccount> Managers { get; set; } = [];
    public List<MandateLimit> Limits { get; set; } = [];
}

namespace KatalepsisFT.Domain.Funds;

/// <summary>A master fund: the top of the fund → sleeve → manager hierarchy.</summary>
public class Fund
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string BaseCurrency { get; set; } = "USD";

    /// <summary>The built-in example fund. Shown with a label and left out of cross-fund totals.</summary>
    public bool IsDemo { get; set; }

    public int? BenchmarkId { get; set; }
    public Benchmarks.Benchmark? Benchmark { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Sleeve> Sleeves { get; set; } = [];
}

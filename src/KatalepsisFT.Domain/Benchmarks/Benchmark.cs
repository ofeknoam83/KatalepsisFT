using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Domain.Benchmarks;

public enum BenchmarkKind
{
    SingleAsset = 1,

    /// <summary>A weighted basket of assets, rebalanced to its weights.</summary>
    Basket = 2,

    /// <summary>A constant annual rate, for example 0% for a market-neutral sleeve.</summary>
    FixedRate = 3,
}

/// <summary>What performance is compared against.</summary>
public class Benchmark
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public BenchmarkKind Kind { get; set; }

    /// <summary>True for a basket that imitates a published index and is not the index itself.</summary>
    public bool IsApproximation { get; set; }

    /// <summary>Annual rate for <see cref="BenchmarkKind.FixedRate"/>.</summary>
    public decimal? FixedAnnualRate { get; set; }
    public List<BenchmarkConstituent> Constituents { get; set; } = [];
}

public class BenchmarkConstituent
{
    public int Id { get; set; }
    public int BenchmarkId { get; set; }
    public Benchmark Benchmark { get; set; } = null!;
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    /// <summary>Fraction of the basket; a single-asset benchmark has one constituent with weight 1.</summary>
    public decimal Weight { get; set; }
}

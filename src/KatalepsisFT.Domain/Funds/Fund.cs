namespace KatalepsisFT.Domain.Funds;

public enum FundKind
{
    /// <summary>A pooled fund of several managers.</summary>
    Fund = 1,

    /// <summary>A separately managed account: one client's own money, run under a mandate.</summary>
    Sma = 2,
}

/// <summary>A master fund or a separately managed account: the top of the fund → sleeve → manager hierarchy.</summary>
public class Fund
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string BaseCurrency { get; set; } = "USD";
    public FundKind Kind { get; set; } = FundKind.Fund;

    /// <summary>For a separately managed account: whose money it is.</summary>
    public string? OwnerName { get; set; }

    /// <summary>The built-in example fund. Shown with a label and left out of cross-fund totals.</summary>
    public bool IsDemo { get; set; }

    /// <summary>
    /// Whether what the fund's paper trading accounts hold counts in its book. Off unless somebody turns it on:
    /// paper money is not the fund's money. On, a paper account is counted whole, its balance included, which is
    /// what lets a trial run show holdings, exposure and risk from the first order.
    /// </summary>
    public bool IncludePaperTrading { get; set; }

    public int? BenchmarkId { get; set; }
    public Benchmarks.Benchmark? Benchmark { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Sleeve> Sleeves { get; set; } = [];
}

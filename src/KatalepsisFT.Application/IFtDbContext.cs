using KatalepsisFT.Domain.Alerts;
using KatalepsisFT.Domain.Benchmarks;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Application;

/// <summary>The database as the application layer sees it.</summary>
public interface IFtDbContext
{
    DbSet<Fund> Funds { get; }
    DbSet<Sleeve> Sleeves { get; }
    DbSet<MandateLimit> MandateLimits { get; }
    DbSet<ManagerAccount> ManagerAccounts { get; }
    DbSet<Asset> Assets { get; }
    DbSet<Instrument> Instruments { get; }
    DbSet<PositionSnapshot> PositionSnapshots { get; }
    DbSet<NavSnapshot> NavSnapshots { get; }
    DbSet<CashFlow> CashFlows { get; }
    DbSet<PriceBar> PriceBars { get; }
    DbSet<Benchmark> Benchmarks { get; }
    DbSet<BenchmarkConstituent> BenchmarkConstituents { get; }
    DbSet<AlertRule> AlertRules { get; }
    DbSet<Alert> Alerts { get; }
    DbSet<AlertAction> AlertActions { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

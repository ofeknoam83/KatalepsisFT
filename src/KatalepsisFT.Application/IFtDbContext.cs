using KatalepsisFT.Domain.Alerts;
using KatalepsisFT.Domain.Benchmarks;
using KatalepsisFT.Domain.Connections;
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
    DbSet<NavSample> NavSamples { get; }
    DbSet<ManualPosition> ManualPositions { get; }
    DbSet<PriceBar> PriceBars { get; }
    DbSet<Benchmark> Benchmarks { get; }
    DbSet<BenchmarkConstituent> BenchmarkConstituents { get; }
    DbSet<VenueConnection> VenueConnections { get; }
    DbSet<AlertRule> AlertRules { get; }
    DbSet<Alert> Alerts { get; }
    DbSet<AlertAction> AlertActions { get; }
    DbSet<KatalepsisFT.Domain.Audit.AuditEntry> AuditEntries { get; }
    DbSet<KatalepsisFT.Domain.Reports.ReportSchedule> ReportSchedules { get; }
    DbSet<KatalepsisFT.Domain.Execution.TradingAccount> TradingAccounts { get; }
    DbSet<KatalepsisFT.Domain.Execution.TradingHalt> TradingHalts { get; }
    DbSet<KatalepsisFT.Domain.Execution.ApprovalRequest> ApprovalRequests { get; }
    DbSet<KatalepsisFT.Domain.Execution.StrategyKey> StrategyKeys { get; }
    DbSet<KatalepsisFT.Domain.Execution.Order> Orders { get; }
    DbSet<KatalepsisFT.Domain.Execution.OrderEvent> OrderEvents { get; }
    DbSet<KatalepsisFT.Domain.Execution.TradingPosition> TradingPositions { get; }
    DbSet<KatalepsisFT.Domain.Execution.SystemSetting> SystemSettings { get; }
    DbSet<KatalepsisFT.Domain.Execution.ListedInstrument> ListedInstruments { get; }
    DbSet<KatalepsisFT.Domain.Execution.PinnedInstrument> PinnedInstruments { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Forgets every change not yet saved, so a unit of work that failed half way can still record that it failed.</summary>
    void DiscardChanges();

    /// <summary>Whether anything loaded through this context has been changed, added or removed and not yet saved.</summary>
    bool HasChanges();

    /// <summary>
    /// Runs the work as one transaction that no other writer of the same manager's book can overlap, in this process
    /// or any other: a second caller waits for the first to finish. Everything saved inside is kept or dropped together.
    /// </summary>
    Task<T> WithManagerLockAsync<T>(int managerAccountId, Func<Task<T>> work, CancellationToken cancellationToken);
}

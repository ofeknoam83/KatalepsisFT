using KatalepsisFT.Application;
using KatalepsisFT.Domain.Alerts;
using KatalepsisFT.Domain.Benchmarks;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using KatalepsisFT.Persistence.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Persistence;

public class FtDbContext(DbContextOptions<FtDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<int>, int>(options),
        IDataProtectionKeyContext,
        IFtDbContext
{
    public void DiscardChanges() => ChangeTracker.Clear();

    public bool HasChanges() => ChangeTracker.HasChanges();

    /// <summary>The first half of the advisory lock key: keeps these locks apart from any other use of advisory locks.</summary>
    private const int ManagerBookLocks = 7301;

    public async Task<T> WithManagerLockAsync<T>(int managerAccountId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        // Already inside a transaction that holds the lock, or one the caller manages.
        if (Database.CurrentTransaction is not null)
        {
            return await work();
        }

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await Database.BeginTransactionAsync(cancellationToken);
        // Held until the transaction ends, so it is released even if the work fails or the process dies.
        await Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ManagerBookLocks}, {managerAccountId})", cancellationToken);
        T result = await work();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>Keys that protect sign-in cookies. Stored here so every web instance can read a session another instance issued.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Fund> Funds => Set<Fund>();
    public DbSet<Sleeve> Sleeves => Set<Sleeve>();
    public DbSet<MandateLimit> MandateLimits => Set<MandateLimit>();
    public DbSet<ManagerAccount> ManagerAccounts => Set<ManagerAccount>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<PositionSnapshot> PositionSnapshots => Set<PositionSnapshot>();
    public DbSet<NavSnapshot> NavSnapshots => Set<NavSnapshot>();
    public DbSet<CashFlow> CashFlows => Set<CashFlow>();
    public DbSet<NavSample> NavSamples => Set<NavSample>();
    public DbSet<ManualPosition> ManualPositions => Set<ManualPosition>();
    public DbSet<PriceBar> PriceBars => Set<PriceBar>();
    public DbSet<Benchmark> Benchmarks => Set<Benchmark>();
    public DbSet<BenchmarkConstituent> BenchmarkConstituents => Set<BenchmarkConstituent>();
    public DbSet<VenueConnection> VenueConnections => Set<VenueConnection>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<KatalepsisFT.Domain.Audit.AuditEntry> AuditEntries => Set<KatalepsisFT.Domain.Audit.AuditEntry>();
    public DbSet<KatalepsisFT.Domain.Reports.ReportSchedule> ReportSchedules => Set<KatalepsisFT.Domain.Reports.ReportSchedule>();
    public DbSet<KatalepsisFT.Domain.Execution.TradingAccount> TradingAccounts => Set<KatalepsisFT.Domain.Execution.TradingAccount>();
    public DbSet<KatalepsisFT.Domain.Execution.TradingHalt> TradingHalts => Set<KatalepsisFT.Domain.Execution.TradingHalt>();
    public DbSet<KatalepsisFT.Domain.Execution.ApprovalRequest> ApprovalRequests => Set<KatalepsisFT.Domain.Execution.ApprovalRequest>();
    public DbSet<KatalepsisFT.Domain.Execution.StrategyKey> StrategyKeys => Set<KatalepsisFT.Domain.Execution.StrategyKey>();
    public DbSet<KatalepsisFT.Domain.Execution.Order> Orders => Set<KatalepsisFT.Domain.Execution.Order>();
    public DbSet<KatalepsisFT.Domain.Execution.OrderEvent> OrderEvents => Set<KatalepsisFT.Domain.Execution.OrderEvent>();
    public DbSet<KatalepsisFT.Domain.Execution.TradingPosition> TradingPositions => Set<KatalepsisFT.Domain.Execution.TradingPosition>();
    public DbSet<KatalepsisFT.Domain.Execution.SystemSetting> SystemSettings => Set<KatalepsisFT.Domain.Execution.SystemSetting>();
    public DbSet<KatalepsisFT.Domain.Execution.ListedInstrument> ListedInstruments => Set<KatalepsisFT.Domain.Execution.ListedInstrument>();
    public DbSet<AlertAction> AlertActions => Set<AlertAction>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(FtDbContext).Assembly);

        builder.Entity<AppUser>(user =>
        {
            user.ToTable("users");
            user.Property(x => x.DisplayName).HasMaxLength(200);
        });
        builder.Entity<IdentityRole<int>>().ToTable("roles");
        builder.Entity<IdentityUserRole<int>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<int>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<int>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<int>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<int>>().ToTable("role_claims");
        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");
    }
}

using KatalepsisFT.Domain.Benchmarks;
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
        IDataProtectionKeyContext
{
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
    public DbSet<PriceBar> PriceBars => Set<PriceBar>();
    public DbSet<Benchmark> Benchmarks => Set<Benchmark>();
    public DbSet<BenchmarkConstituent> BenchmarkConstituents => Set<BenchmarkConstituent>();

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

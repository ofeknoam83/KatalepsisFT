using KatalepsisFT.Domain.Benchmarks;
using KatalepsisFT.Domain.Funds;
using KatalepsisFT.Domain.Instruments;
using KatalepsisFT.Domain.Positions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KatalepsisFT.Persistence.Configurations;

/// <summary>Column types shared by every table: 8 decimals everywhere, more whole digits for quantities.</summary>
internal static class Precision
{
    public static PropertyBuilder<decimal> Quantity(this PropertyBuilder<decimal> property) => property.HasPrecision(28, 8);

    public static PropertyBuilder<decimal> Price(this PropertyBuilder<decimal> property) => property.HasPrecision(18, 8);

    public static PropertyBuilder<decimal?> Price(this PropertyBuilder<decimal?> property) => property.HasPrecision(18, 8);

    public static PropertyBuilder<decimal> Usd(this PropertyBuilder<decimal> property) => property.HasPrecision(18, 2);

    public static PropertyBuilder<decimal> Ratio(this PropertyBuilder<decimal> property) => property.HasPrecision(18, 8);

    public static PropertyBuilder<decimal?> Ratio(this PropertyBuilder<decimal?> property) => property.HasPrecision(18, 8);
}

internal sealed class FundConfiguration : IEntityTypeConfiguration<Fund>
{
    public void Configure(EntityTypeBuilder<Fund> fund)
    {
        fund.ToTable("funds");
        fund.Property(x => x.Name).HasMaxLength(200);
        fund.Property(x => x.BaseCurrency).HasMaxLength(8);
        fund.HasIndex(x => x.Name).IsUnique();
        fund.HasOne(x => x.Benchmark).WithMany().HasForeignKey(x => x.BenchmarkId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class SleeveConfiguration : IEntityTypeConfiguration<Sleeve>
{
    public void Configure(EntityTypeBuilder<Sleeve> sleeve)
    {
        sleeve.ToTable("sleeves");
        sleeve.Property(x => x.Name).HasMaxLength(200);
        sleeve.HasIndex(x => new { x.FundId, x.Name }).IsUnique();
        sleeve.HasOne(x => x.Fund).WithMany(x => x.Sleeves).HasForeignKey(x => x.FundId).OnDelete(DeleteBehavior.Cascade);
        sleeve.HasOne(x => x.Benchmark).WithMany().HasForeignKey(x => x.BenchmarkId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class MandateLimitConfiguration : IEntityTypeConfiguration<MandateLimit>
{
    public void Configure(EntityTypeBuilder<MandateLimit> limit)
    {
        limit.ToTable("mandate_limits");
        limit.Property(x => x.Value).Ratio();
        limit.HasIndex(x => new { x.SleeveId, x.Kind }).IsUnique();
        limit.HasOne(x => x.Sleeve).WithMany(x => x.Limits).HasForeignKey(x => x.SleeveId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ManagerAccountConfiguration : IEntityTypeConfiguration<ManagerAccount>
{
    public void Configure(EntityTypeBuilder<ManagerAccount> manager)
    {
        manager.ToTable("manager_accounts");
        manager.Property(x => x.Name).HasMaxLength(200);
        manager.Property(x => x.AllocatedCapitalUsd).Usd();
        manager.HasIndex(x => new { x.SleeveId, x.Name }).IsUnique();
        manager.HasOne(x => x.Sleeve).WithMany(x => x.Managers).HasForeignKey(x => x.SleeveId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> asset)
    {
        asset.ToTable("assets");
        asset.Property(x => x.Symbol).HasMaxLength(32);
        asset.Property(x => x.Name).HasMaxLength(200);
        asset.HasIndex(x => x.Symbol).IsUnique();
    }
}

internal sealed class InstrumentConfiguration : IEntityTypeConfiguration<Instrument>
{
    public void Configure(EntityTypeBuilder<Instrument> instrument)
    {
        instrument.ToTable("instruments");
        instrument.Property(x => x.Symbol).HasMaxLength(100);
        instrument.Property(x => x.Multiplier).Quantity();
        instrument.Property(x => x.Strike).Price();
        instrument.Ignore(x => x.IsDerivative);
        instrument.HasIndex(x => x.Symbol).IsUnique();
        instrument.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PositionSnapshotConfiguration : IEntityTypeConfiguration<PositionSnapshot>
{
    public void Configure(EntityTypeBuilder<PositionSnapshot> position)
    {
        position.ToTable("position_snapshots");
        position.Property(x => x.Quantity).Quantity();
        position.Property(x => x.EntryPrice).Price();
        position.Property(x => x.MarkPrice).Price();
        position.Property(x => x.UnderlyingPrice).Price();
        position.Property(x => x.Delta).Ratio();
        position.Property(x => x.MarketValueUsd).Usd();
        position.Property(x => x.ExposureUsd).Usd();
        position.Property(x => x.UnrealizedPnlUsd).Usd();
        position.HasIndex(x => new { x.ManagerAccountId, x.Date, x.InstrumentId }).IsUnique();
        position.HasIndex(x => new { x.Date, x.InstrumentId });
        position.HasOne(x => x.ManagerAccount).WithMany().HasForeignKey(x => x.ManagerAccountId).OnDelete(DeleteBehavior.Cascade);
        position.HasOne(x => x.Instrument).WithMany().HasForeignKey(x => x.InstrumentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NavSnapshotConfiguration : IEntityTypeConfiguration<NavSnapshot>
{
    public void Configure(EntityTypeBuilder<NavSnapshot> nav)
    {
        nav.ToTable("nav_snapshots");
        nav.Property(x => x.NavUsd).Usd();
        nav.HasIndex(x => new { x.ManagerAccountId, x.Date }).IsUnique();
        nav.HasOne(x => x.ManagerAccount).WithMany().HasForeignKey(x => x.ManagerAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CashFlowConfiguration : IEntityTypeConfiguration<CashFlow>
{
    public void Configure(EntityTypeBuilder<CashFlow> flow)
    {
        flow.ToTable("cash_flows");
        flow.Property(x => x.AmountUsd).Usd();
        flow.Property(x => x.Note).HasMaxLength(500);
        flow.HasIndex(x => new { x.ManagerAccountId, x.Date });
        flow.HasOne(x => x.ManagerAccount).WithMany().HasForeignKey(x => x.ManagerAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PriceBarConfiguration : IEntityTypeConfiguration<PriceBar>
{
    public void Configure(EntityTypeBuilder<PriceBar> bar)
    {
        bar.ToTable("price_bars");
        bar.Property(x => x.Close).Price();
        bar.Property(x => x.VolumeUsd).Usd();
        bar.Property(x => x.Source).HasMaxLength(32);
        bar.HasIndex(x => new { x.AssetId, x.Date }).IsUnique();
        bar.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BenchmarkConfiguration : IEntityTypeConfiguration<Benchmark>
{
    public void Configure(EntityTypeBuilder<Benchmark> benchmark)
    {
        benchmark.ToTable("benchmarks");
        benchmark.Property(x => x.Name).HasMaxLength(200);
        benchmark.Property(x => x.FixedAnnualRate).Ratio();
        benchmark.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class BenchmarkConstituentConfiguration : IEntityTypeConfiguration<BenchmarkConstituent>
{
    public void Configure(EntityTypeBuilder<BenchmarkConstituent> constituent)
    {
        constituent.ToTable("benchmark_constituents");
        constituent.Property(x => x.Weight).Ratio();
        constituent.HasIndex(x => new { x.BenchmarkId, x.AssetId }).IsUnique();
        constituent.HasOne(x => x.Benchmark).WithMany(x => x.Constituents).HasForeignKey(x => x.BenchmarkId).OnDelete(DeleteBehavior.Cascade);
        constituent.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
    }
}

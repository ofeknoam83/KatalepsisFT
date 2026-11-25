using KatalepsisFT.Domain.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KatalepsisFT.Persistence.Configurations;

internal sealed class TradingAccountConfiguration : IEntityTypeConfiguration<TradingAccount>
{
    public void Configure(EntityTypeBuilder<TradingAccount> account)
    {
        account.ToTable("trading_accounts");
        account.Property(x => x.Label).HasMaxLength(100);
        account.Property(x => x.KeyHint).HasMaxLength(8);
        account.Property(x => x.CreatedBy).HasMaxLength(256);
        account.Property(x => x.Leverage).HasPrecision(6, 2);
        account.Property(x => x.MaxOrderNotionalUsd).HasPrecision(20, 2);
        account.Property(x => x.MaxPositionNotionalUsd).HasPrecision(20, 2);
        account.Property(x => x.PaperBalanceUsd).HasPrecision(20, 2);
        account.Property(x => x.LiveEquityUsd).HasPrecision(20, 2);
        account.Property(x => x.LiveSyncError).HasMaxLength(500);
        // One account per manager and venue: two would let the same money be traded twice.
        account.HasIndex(x => new { x.ManagerAccountId, x.Venue }).IsUnique();
        account.HasOne(x => x.ManagerAccount).WithMany().HasForeignKey(x => x.ManagerAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TradingHaltConfiguration : IEntityTypeConfiguration<TradingHalt>
{
    public void Configure(EntityTypeBuilder<TradingHalt> halt)
    {
        halt.ToTable("trading_halts");
        halt.Property(x => x.Reason).HasMaxLength(500);
        halt.Property(x => x.PulledBy).HasMaxLength(256);
        halt.Property(x => x.ReleasedBy).HasMaxLength(256);
        halt.Ignore(x => x.IsActive);
        halt.HasIndex(x => x.ReleasedAt);
    }
}

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> request)
    {
        request.ToTable("approval_requests");
        request.Property(x => x.Summary).HasMaxLength(600);
        request.Property(x => x.RequestedBy).HasMaxLength(256);
        request.Property(x => x.DecidedBy).HasMaxLength(256);
        request.Property(x => x.Note).HasMaxLength(1000);
        request.HasIndex(x => x.Status);
    }
}

internal sealed class StrategyKeyConfiguration : IEntityTypeConfiguration<StrategyKey>
{
    public void Configure(EntityTypeBuilder<StrategyKey> key)
    {
        key.ToTable("strategy_keys");
        key.Property(x => x.Name).HasMaxLength(100);
        key.Property(x => x.Prefix).HasMaxLength(16);
        key.Property(x => x.KeyHash).HasMaxLength(64);
        key.Property(x => x.AccountIds).HasMaxLength(2000);
        key.Property(x => x.CreatedBy).HasMaxLength(256);
        key.Property(x => x.MaxOrderNotionalUsd).HasPrecision(20, 2);
        key.HasIndex(x => x.KeyHash).IsUnique();
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> order)
    {
        order.ToTable("orders");
        order.Property(x => x.ClientOrderId).HasMaxLength(64);
        order.Property(x => x.Asset).HasMaxLength(32);
        order.Property(x => x.SubmittedBy).HasMaxLength(256);
        order.Property(x => x.RejectReason).HasMaxLength(500);
        order.Property(x => x.Quantity).HasPrecision(28, 10);
        order.Property(x => x.FilledQuantity).HasPrecision(28, 10);
        order.Property(x => x.LimitPrice).HasPrecision(28, 10);
        order.Property(x => x.AverageFillPrice).HasPrecision(28, 10);
        order.Property(x => x.ArrivalPrice).HasPrecision(28, 10);
        order.Property(x => x.FeesUsd).HasPrecision(20, 6);
        order.Property(x => x.VenueClientId).HasMaxLength(40);
        order.Property(x => x.VenueOrderId).HasMaxLength(64);
        // Postgres' own change counter of the row: a save made from an older reading of the order fails.
        order.Property(x => x.Version).IsRowVersion();
        order.Ignore(x => x.IsOpen);
        order.Ignore(x => x.RemainingQuantity);
        // The same name from the same sender on the same account is the same order.
        order.HasIndex(x => new { x.TradingAccountId, x.ClientOrderId }).IsUnique();
        order.HasIndex(x => x.Status);
        order.HasIndex(x => x.CreatedAt);
        order.HasOne(x => x.TradingAccount).WithMany().HasForeignKey(x => x.TradingAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderEventConfiguration : IEntityTypeConfiguration<OrderEvent>
{
    public void Configure(EntityTypeBuilder<OrderEvent> orderEvent)
    {
        orderEvent.ToTable("order_events");
        orderEvent.Property(x => x.Detail).HasMaxLength(500);
        orderEvent.Property(x => x.Quantity).HasPrecision(28, 10);
        orderEvent.Property(x => x.Price).HasPrecision(28, 10);
        orderEvent.Property(x => x.FeeUsd).HasPrecision(20, 6);
        orderEvent.HasOne(x => x.Order).WithMany(x => x.Events).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TradingPositionConfiguration : IEntityTypeConfiguration<TradingPosition>
{
    public void Configure(EntityTypeBuilder<TradingPosition> position)
    {
        position.ToTable("trading_positions");
        position.Property(x => x.Asset).HasMaxLength(32);
        position.Property(x => x.Quantity).HasPrecision(28, 10);
        position.Property(x => x.AveragePrice).HasPrecision(28, 10);
        position.Property(x => x.RealisedPnlUsd).HasPrecision(20, 6);
        position.Property(x => x.FeesUsd).HasPrecision(20, 6);
        position.Property(x => x.LiquidationPrice).HasPrecision(28, 10);
        position.HasIndex(x => new { x.TradingAccountId, x.Asset, x.InstrumentType }).IsUnique();
        position.HasOne(x => x.TradingAccount).WithMany().HasForeignKey(x => x.TradingAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> setting)
    {
        setting.ToTable("system_settings");
        setting.Property(x => x.Name).HasMaxLength(100);
        setting.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class ListedInstrumentConfiguration : IEntityTypeConfiguration<ListedInstrument>
{
    public void Configure(EntityTypeBuilder<ListedInstrument> instrument)
    {
        instrument.ToTable("listed_instruments");
        instrument.Property(x => x.Asset).HasMaxLength(32);
        instrument.Property(x => x.Quote).HasMaxLength(12);
        instrument.Property(x => x.Symbol).HasMaxLength(64);
        instrument.HasIndex(x => new { x.Venue, x.Type, x.Asset }).IsUnique();
        // What the search asks for: an asset by the start of its name.
        instrument.HasIndex(x => x.Asset);
    }
}

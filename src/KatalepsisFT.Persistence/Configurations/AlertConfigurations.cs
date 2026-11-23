using KatalepsisFT.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KatalepsisFT.Persistence.Configurations;

internal sealed class AlertRuleConfiguration : IEntityTypeConfiguration<AlertRule>
{
    public void Configure(EntityTypeBuilder<AlertRule> rule)
    {
        rule.ToTable("alert_rules");
        rule.Property(x => x.Threshold).Ratio();
        rule.HasIndex(x => x.MandateLimitId).IsUnique();
        rule.HasOne(x => x.Fund).WithMany().HasForeignKey(x => x.FundId).OnDelete(DeleteBehavior.Cascade);
        rule.HasOne(x => x.MandateLimit).WithMany().HasForeignKey(x => x.MandateLimitId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> alert)
    {
        alert.ToTable("alerts");
        alert.Property(x => x.Subject).HasMaxLength(200);
        alert.Property(x => x.AssigneeName).HasMaxLength(256);
        alert.Property(x => x.Value).Ratio();
        alert.Property(x => x.Threshold).Ratio();
        alert.Property(x => x.PeakValue).Ratio();
        alert.Ignore(x => x.IsActive);
        alert.HasIndex(x => new { x.RuleId, x.Status });
        alert.HasOne(x => x.Rule).WithMany(x => x.Alerts).HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlertActionConfiguration : IEntityTypeConfiguration<AlertAction>
{
    public void Configure(EntityTypeBuilder<AlertAction> action)
    {
        action.ToTable("alert_actions");
        action.Property(x => x.UserName).HasMaxLength(256);
        action.Property(x => x.Note).HasMaxLength(2000);
        action.HasOne(x => x.Alert).WithMany(x => x.Actions).HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<KatalepsisFT.Domain.Audit.AuditEntry>
{
    public void Configure(EntityTypeBuilder<KatalepsisFT.Domain.Audit.AuditEntry> entry)
    {
        entry.ToTable("audit_entries");
        entry.Property(x => x.UserName).HasMaxLength(256);
        entry.Property(x => x.Action).HasMaxLength(100);
        entry.Property(x => x.FundName).HasMaxLength(200);
        entry.Property(x => x.Subject).HasMaxLength(400);
        entry.Property(x => x.Detail).HasMaxLength(2000);
        // No foreign key to the fund: an entry outlives what it is about.
        entry.HasIndex(x => x.At);
        entry.HasIndex(x => new { x.FundId, x.At });
    }
}

internal sealed class ReportScheduleConfiguration : IEntityTypeConfiguration<KatalepsisFT.Domain.Reports.ReportSchedule>
{
    public void Configure(EntityTypeBuilder<KatalepsisFT.Domain.Reports.ReportSchedule> schedule)
    {
        schedule.ToTable("report_schedules");
        schedule.Property(x => x.Recipients).HasMaxLength(6000);
        schedule.Property(x => x.LastError).HasMaxLength(500);
        schedule.Property(x => x.CreatedBy).HasMaxLength(256);
        schedule.HasOne(x => x.Fund).WithMany().HasForeignKey(x => x.FundId).OnDelete(DeleteBehavior.Cascade);
    }
}

using KatalepsisFT.Domain.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KatalepsisFT.Persistence.Configurations;

internal sealed class VenueConnectionConfiguration : IEntityTypeConfiguration<VenueConnection>
{
    public void Configure(EntityTypeBuilder<VenueConnection> connection)
    {
        connection.ToTable("venue_connections");
        connection.Property(x => x.Label).HasMaxLength(100);
        connection.Property(x => x.KeyHint).HasMaxLength(8);
        connection.Property(x => x.LastError).HasMaxLength(500);
        connection.Property(x => x.TransfersError).HasMaxLength(500);
        connection.Property(x => x.CreatedBy).HasMaxLength(256);
        connection.Property(x => x.LastSnapshotJson).HasColumnType("jsonb");
        connection.HasIndex(x => x.ManagerAccountId);
        connection.HasOne(x => x.ManagerAccount).WithMany().HasForeignKey(x => x.ManagerAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

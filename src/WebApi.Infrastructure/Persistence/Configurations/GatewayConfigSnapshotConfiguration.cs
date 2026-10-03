using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class GatewayConfigSnapshotConfiguration : IEntityTypeConfiguration<GatewayConfigSnapshot>
{
    public void Configure(EntityTypeBuilder<GatewayConfigSnapshot> b)
    {
        b.ToTable("gateway_config_snapshots"); b.HasKey(x => x.ConfigVersionId);
        b.Property(x => x.ConfigVersionId).HasColumnName("config_version_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.PayloadBytes).HasColumnName("payload_bytes").HasColumnType("bytea").IsRequired();
    }
}

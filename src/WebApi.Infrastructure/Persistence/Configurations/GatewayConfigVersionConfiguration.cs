using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class GatewayConfigVersionConfiguration : IEntityTypeConfiguration<GatewayConfigVersion>
{
    public void Configure(EntityTypeBuilder<GatewayConfigVersion> b)
    {
        b.ToTable("gateway_config_versions"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.VersionNo).HasColumnName("version_no").HasColumnType("bigint").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.SnapshotKey).HasColumnName("snapshot_key").HasColumnType("varchar(256)");
        b.Property(x => x.SnapshotHash).HasColumnName("snapshot_hash").HasColumnType("varchar(128)");
        b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamptz");
    }
}

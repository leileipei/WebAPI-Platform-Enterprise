using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleaseTargetConfiguration : IEntityTypeConfiguration<ReleaseTarget>
{
    public void Configure(EntityTypeBuilder<ReleaseTarget> b)
    {
        b.ToTable("release_targets"); b.HasKey(x => new { x.ReleaseId, x.NodeId });
        b.Property(x => x.ReleaseId).HasColumnName("release_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.NodeId).HasColumnName("node_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.InstanceId).HasColumnName("instance_id").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
    }
}

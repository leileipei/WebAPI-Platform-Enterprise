using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class GatewayAckConfiguration : IEntityTypeConfiguration<GatewayAck>
{
    public void Configure(EntityTypeBuilder<GatewayAck> b)
    {
        b.ToTable("gateway_acks"); b.HasKey(x => new { x.ReleaseId, x.NodeId });
        b.Property(x => x.ReleaseId).HasColumnName("release_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.NodeId).HasColumnName("node_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.InstanceId).HasColumnName("instance_id").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.ConfigVersion).HasColumnName("config_version").HasColumnType("bigint").IsRequired();
        b.Property(x => x.DeploymentSequence).HasColumnName("deployment_sequence").HasColumnType("bigint").IsRequired().HasDefaultValue(0L);
        b.Property(x => x.PayloadHash).HasColumnName("payload_hash").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.AppliedAt).HasColumnName("applied_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.Success).HasColumnName("success").HasColumnType("boolean").IsRequired();
        b.Property(x => x.ErrorCode).HasColumnName("error_code").HasColumnType("varchar(128)");
    }
}

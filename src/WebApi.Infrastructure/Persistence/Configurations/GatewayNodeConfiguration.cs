using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class GatewayNodeConfiguration : IEntityTypeConfiguration<GatewayNode>
{
    public void Configure(EntityTypeBuilder<GatewayNode> b)
    {
        b.ToTable("gateway_nodes"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.NodeName).HasColumnName("node_name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.InstanceId).HasColumnName("instance_id").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.AppVersion).HasColumnName("app_version").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.CurrentConfigVersion).HasColumnName("current_config_version").HasColumnType("bigint").IsRequired();
        b.Property(x => x.TargetConfigVersion).HasColumnName("target_config_version").HasColumnType("bigint");
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.LastHeartbeatAt).HasColumnName("last_heartbeat_at").HasColumnType("timestamptz");
        b.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        b.Property(x => x.CurrentDeploymentSequence).HasColumnName("current_deployment_sequence").HasColumnType("bigint").IsRequired().HasDefaultValue(0L);
        b.Property(x => x.IdentityHash).HasColumnName("identity_hash").HasColumnType("varchar(256)").IsRequired();
        b.Property(x => x.Enabled).HasColumnName("enabled").HasColumnType("boolean").IsRequired().HasDefaultValue(true);
    }
}

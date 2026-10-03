using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class UpstreamClusterConfiguration : IEntityTypeConfiguration<UpstreamCluster>
{
    public void Configure(EntityTypeBuilder<UpstreamCluster> b)
    {
        b.ToTable("upstream_clusters"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ProjectId).HasColumnName("project_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.LoadBalancingPolicy).HasColumnName("load_balancing_policy").HasColumnType("varchar(32)").IsRequired();
        b.Property(x => x.HealthCheckEnabled).HasColumnName("health_check_enabled").HasColumnType("boolean").IsRequired();
        b.Property(x => x.HealthCheckPath).HasColumnName("health_check_path").HasColumnType("varchar(256)").IsRequired();
        b.Property(x => x.HealthCheckIntervalSec).HasColumnName("health_check_interval_sec").HasColumnType("int").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

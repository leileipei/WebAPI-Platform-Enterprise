using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class EnvironmentRecordConfiguration : IEntityTypeConfiguration<EnvironmentRecord>
{
    public void Configure(EntityTypeBuilder<EnvironmentRecord> b)
    {
        b.ToTable("environments"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ProjectId).HasColumnName("project_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Code).HasColumnName("code").HasColumnType("varchar(32)").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.IsProduction).HasColumnName("is_production").HasColumnType("boolean").IsRequired();
        b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("int").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.ReleasePolicyId).HasColumnName("release_policy_id").HasColumnType("uuid");
        b.Property(x => x.DesiredConfigVersion).HasColumnName("desired_config_version").HasColumnType("bigint");
        b.Property(x => x.DeploymentSequence).HasColumnName("deployment_sequence").HasColumnType("bigint").IsRequired().HasDefaultValue(0L);
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

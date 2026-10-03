using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class PolicyConfiguration : IEntityTypeConfiguration<Policy>
{
    public void Configure(EntityTypeBuilder<Policy> b)
    {
        b.ToTable("policies"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.Type).HasColumnName("type").HasColumnType("varchar(48)").IsRequired();
        b.Property(x => x.Config).HasColumnName("config").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Enabled).HasColumnName("enabled").HasColumnType("boolean").IsRequired().HasDefaultValue(true);
        b.Property(x => x.VersionNo).HasColumnName("version_no").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired();
    }
}

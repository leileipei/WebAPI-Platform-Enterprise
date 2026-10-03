using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiConfiguration : IEntityTypeConfiguration<Api>
{
    public void Configure(EntityTypeBuilder<Api> b)
    {
        b.ToTable("apis"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ProjectId).HasColumnName("project_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.GroupId).HasColumnName("group_id").HasColumnType("uuid");
        b.Property(x => x.Code).HasColumnName("code").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasColumnType("text");
        b.Property(x => x.LifecycleStatus).HasColumnName("lifecycle_status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.OwnerUserId).HasColumnName("owner_user_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.VersionNo).HasColumnName("version_no").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

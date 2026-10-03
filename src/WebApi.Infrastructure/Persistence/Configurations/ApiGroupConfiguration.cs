using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiGroupConfiguration : IEntityTypeConfiguration<ApiGroup>
{
    public void Configure(EntityTypeBuilder<ApiGroup> b)
    {
        b.ToTable("api_groups"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ProjectId).HasColumnName("project_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.ParentId).HasColumnName("parent_id").HasColumnType("uuid");
        b.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("int").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

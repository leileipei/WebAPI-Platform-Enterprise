using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleaseItemConfiguration : IEntityTypeConfiguration<ReleaseItem>
{
    public void Configure(EntityTypeBuilder<ReleaseItem> b)
    {
        b.ToTable("release_items"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ReleaseId).HasColumnName("release_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ResourceType).HasColumnName("resource_type").HasColumnType("varchar(32)").IsRequired();
        b.Property(x => x.ResourceId).HasColumnName("resource_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ChangeType).HasColumnName("change_type").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb");
        b.Property(x => x.AfterJson).HasColumnName("after_json").HasColumnType("jsonb");
    }
}

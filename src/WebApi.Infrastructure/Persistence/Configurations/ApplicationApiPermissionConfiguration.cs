using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApplicationApiPermissionConfiguration : IEntityTypeConfiguration<ApplicationApiPermission>
{
    public void Configure(EntityTypeBuilder<ApplicationApiPermission> b)
    {
        b.ToTable("application_api_permissions"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ApplicationId).HasColumnName("application_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ApiId).HasColumnName("api_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ValidFrom).HasColumnName("valid_from").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
        b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

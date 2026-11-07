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
        b.Property(x => x.Mode).HasColumnName("mode").HasMaxLength(24).IsRequired().HasDefaultValue("RouteMethod");
        b.Property<long>("RevisionTransactionId").HasColumnName("revision_transaction_id").HasColumnType("bigint").HasDefaultValueSql("txid_current()").ValueGeneratedOnAddOrUpdate();
        b.Property<Guid>("OrganizationId").HasColumnName("organization_id").HasColumnType("uuid").HasDefaultValueSql("'00000000-0000-0000-0000-000000000000'::uuid");
        b.Property<Guid>("ProjectId").HasColumnName("project_id").HasColumnType("uuid").HasDefaultValueSql("'00000000-0000-0000-0000-000000000000'::uuid");
        b.HasOne<ApplicationRecord>().WithMany().HasForeignKey("ApplicationId", "OrganizationId").HasPrincipalKey("Id", "OrganizationId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Api>().WithMany().HasForeignKey("ApiId", "ProjectId", "OrganizationId").HasPrincipalKey("Id", "ProjectId", "OrganizationId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EnvironmentRecord>().WithMany().HasForeignKey("EnvironmentId", "ProjectId").HasPrincipalKey("Id", "ProjectId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey("ProjectId", "OrganizationId").HasPrincipalKey("Id", "OrganizationId").OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.Id, x.EnvironmentId, x.ApiId });
        b.ToTable(t => t.HasCheckConstraint("ck_permission_mode", "mode IN ('LegacyApi','RouteMethod')"));
        b.Property(x => x.ValidFrom).HasColumnName("valid_from").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
        b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.ExpiryRevision).HasColumnName("expiry_revision").HasColumnType("bigint").IsRequired().HasDefaultValue(1L);
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

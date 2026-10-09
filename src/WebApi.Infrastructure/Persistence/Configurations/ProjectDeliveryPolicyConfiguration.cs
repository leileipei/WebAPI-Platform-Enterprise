using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ProjectDeliveryPolicyConfiguration : IEntityTypeConfiguration<ProjectDeliveryPolicy>
{
    public void Configure(EntityTypeBuilder<ProjectDeliveryPolicy> b)
    {
        b.ToTable("project_delivery_policies");b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.SourceEnvironmentId).HasColumnName("source_environment_id").HasColumnType("uuid");
        b.Property(x=>x.TargetEnvironmentId).HasColumnName("target_environment_id").HasColumnType("uuid");
        b.Property(x=>x.Mode).HasColumnName("mode").HasColumnType("varchar(32)").IsRequired().HasDefaultValue("Legacy");
        b.Property(x=>x.RequiredTestTypes).HasColumnName("required_test_types").HasColumnType("text[]").IsRequired().HasDefaultValueSql("ARRAY['InterfaceFunction','Integration','ContractCompatibility']::text[]");
        b.Property(x=>x.VerificationValidityMinutes).HasColumnName("verification_validity_minutes").HasColumnType("integer").HasDefaultValue(1440);
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken().HasDefaultValue(1L);
        b.Property(x=>x.UpdatedBy).HasColumnName("updated_by").HasColumnType("uuid");
        b.Property(x=>x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>x.ProjectId).IsUnique();
        b.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x=>new{x.SourceEnvironmentId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x=>new{x.TargetEnvironmentId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_delivery_policy", "mode IN ('Legacy','PromotionRequired') AND source_environment_id <> target_environment_id AND verification_validity_minutes BETWEEN 1 AND 10080 AND revision >= 1 AND cardinality(required_test_types) BETWEEN 1 AND 3 AND required_test_types <@ ARRAY['InterfaceFunction','Integration','ContractCompatibility']::text[]"));
    }
}

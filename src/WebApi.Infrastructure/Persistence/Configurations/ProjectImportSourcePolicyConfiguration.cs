using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ProjectImportSourcePolicyConfiguration : IEntityTypeConfiguration<ProjectImportSourcePolicy>
{
    public void Configure(EntityTypeBuilder<ProjectImportSourcePolicy> b)
    {
        b.ToTable("project_import_source_policies", t => t.HasCheckConstraint("ck_import_policy_revision", "revision >= 1")); b.HasKey(x => x.ProjectId);
        b.Property(x => x.ProjectId).HasColumnName("project_id"); b.Property(x => x.RulesJson).HasColumnName("rules_json").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken(); b.Property(x => x.UpdatedBy).HasColumnName("updated_by"); b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict); b.HasOne<UserRecord>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

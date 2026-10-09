using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleasePromotionConfiguration : IEntityTypeConfiguration<ReleasePromotion>
{
    public void Configure(EntityTypeBuilder<ReleasePromotion> b)
    {
        b.ToTable("release_promotions");b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.ArtifactId).HasColumnName("artifact_id").HasColumnType("uuid");
        b.Property(x=>x.SourceEnvironmentId).HasColumnName("source_environment_id").HasColumnType("uuid");
        b.Property(x=>x.TargetEnvironmentId).HasColumnName("target_environment_id").HasColumnType("uuid");
        b.Property(x=>x.SourceReleaseId).HasColumnName("source_release_id").HasColumnType("uuid");
        b.Property(x=>x.TargetReleaseId).HasColumnName("target_release_id").HasColumnType("uuid");
        b.Property(x=>x.AcceptanceId).HasColumnName("acceptance_id").HasColumnType("uuid");
        b.Property(x=>x.Status).HasColumnName("status").HasColumnType("varchar(32)").IsRequired().HasDefaultValue("Draft");
        b.Property(x=>x.BaselineConfigVersion).HasColumnName("baseline_config_version").HasColumnType("bigint").HasDefaultValue(0L);
        b.Property(x=>x.MappingRevision).HasColumnName("mapping_revision").HasColumnType("bigint").HasDefaultValue(0L);
        b.Property(x=>x.CandidateHash).HasColumnName("candidate_hash").HasColumnType("varchar(64)");
        b.Property(x=>x.ResourceRevisionsJson).HasColumnName("resource_revisions_json").HasColumnType("jsonb").IsRequired().HasDefaultValue("[]");
        b.Property(x=>x.FrozenPolicyJson).HasColumnName("frozen_policy_json").HasColumnType("jsonb").IsRequired().HasDefaultValue("{}");
        b.Property(x=>x.PrecheckJson).HasColumnName("precheck_json").HasColumnType("jsonb");
        b.Property(x=>x.TargetAccessAddressRevision).HasColumnName("target_access_address_revision").HasColumnType("bigint").HasDefaultValue(0L);
        b.Property(x=>x.VerificationContextJson).HasColumnName("verification_context_json").HasColumnType("jsonb");
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken().HasDefaultValue(1L);
        b.Property(x=>x.RequestedBy).HasColumnName("requested_by").HasColumnType("uuid");
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        b.Property(x=>x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamptz");
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.ProjectId,x.TargetEnvironmentId});b.HasAlternateKey(x=>new{x.Id,x.ArtifactId,x.SourceReleaseId,x.TargetEnvironmentId});
        b.HasIndex(x=>new{x.ProjectId,x.CreatedAt,x.Id}).IsDescending(false,true,true);
        b.HasOne<ReleaseArtifact>().WithMany().HasForeignKey(x=>new{x.ArtifactId,x.ProjectId,x.SourceEnvironmentId,x.SourceReleaseId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId,x.SourceReleaseId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x=>new{x.TargetEnvironmentId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseRecord>().WithMany().HasForeignKey(x=>new{x.TargetReleaseId,x.TargetEnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseTestAcceptance>().WithMany().HasForeignKey(x=>new{x.AcceptanceId,x.ProjectId,x.SourceEnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_promotion_state", "source_environment_id <> target_environment_id AND revision >= 1 AND mapping_revision >= 0 AND status IN ('Draft','WaitingApproval','Ready','Deploying','Verifying','Completed','Rejected','Cancelled','Invalidated','DeploymentFailed','VerificationFailed','RolledBack')"));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleaseTestAcceptanceConfiguration : IEntityTypeConfiguration<ReleaseTestAcceptance>
{
    public void Configure(EntityTypeBuilder<ReleaseTestAcceptance> b)
    {
        b.Property(x=>x.PipelineRunStageId).HasColumnName("pipeline_run_stage_id").HasColumnType("uuid");
        b.Property(x=>x.StageAttemptId).HasColumnName("stage_attempt_id").HasColumnType("uuid");
        b.Property(x=>x.ProfileHash).HasColumnName("profile_hash").HasColumnType("varchar(64)");
        b.HasOne<ReleasePipelineRunStage>().WithMany().HasForeignKey(x=>new{x.PipelineRunStageId,x.ProjectId,x.SourceEnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleasePipelineStageAttempt>().WithMany().HasForeignKey(x=>new{x.StageAttemptId,x.PipelineRunStageId}).HasPrincipalKey(x=>new{x.Id,x.RunStageId}).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_release_test_acceptance_pipeline_context", "((pipeline_run_stage_id IS NULL AND stage_attempt_id IS NULL AND profile_hash IS NULL) OR (pipeline_run_stage_id IS NOT NULL AND stage_attempt_id IS NOT NULL AND profile_hash IS NOT NULL AND length(profile_hash)=64))"));
        b.ToTable("release_test_acceptances");b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.ArtifactId).HasColumnName("artifact_id").HasColumnType("uuid");
        b.Property(x=>x.SourceEnvironmentId).HasColumnName("source_environment_id").HasColumnType("uuid");
        b.Property(x=>x.VerificationIds).HasColumnName("verification_ids").HasColumnType("uuid[]").IsRequired().HasDefaultValueSql("ARRAY[]::uuid[]");
        b.Property(x=>x.EvidenceHash).HasColumnName("evidence_hash").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.PolicyRevision).HasColumnName("policy_revision").HasColumnType("bigint");
        b.Property(x=>x.Status).HasColumnName("status").HasColumnType("varchar(32)").IsRequired().HasDefaultValue("Requested");
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken().HasDefaultValue(1L);
        b.Property(x=>x.RequestedBy).HasColumnName("requested_by").HasColumnType("uuid");
        b.Property(x=>x.ActedBy).HasColumnName("acted_by").HasColumnType("uuid");
        b.Property(x=>x.Comment).HasColumnName("comment").HasColumnType("text").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        b.Property(x=>x.ActedAt).HasColumnName("acted_at").HasColumnType("timestamptz");
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId});
        b.HasOne<ReleaseArtifact>().WithMany().HasForeignKey(x=>new{x.ArtifactId,x.ProjectId,x.SourceEnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.RequestedBy).OnDelete(DeleteBehavior.Restrict);b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.ActedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_test_acceptance", "status IN ('Requested','Accepted','Rejected','Revoked') AND revision >= 1 AND (acted_by IS NULL OR acted_by <> requested_by)"));
    }
}

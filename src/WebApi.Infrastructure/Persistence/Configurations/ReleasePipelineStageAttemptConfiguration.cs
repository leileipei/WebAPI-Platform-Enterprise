using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleasePipelineStageAttemptConfiguration : IEntityTypeConfiguration<ReleasePipelineStageAttempt>
{
    public void Configure(EntityTypeBuilder<ReleasePipelineStageAttempt> b)
    {
        b.ToTable("release_pipeline_stage_attempts"); b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()");
        b.Property(x=>x.RunStageId).HasColumnName("run_stage_id").HasColumnType("uuid");
        b.Property(x=>x.RunId).HasColumnName("run_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid");
        b.Property(x=>x.AttemptNo).HasColumnName("attempt_no").HasColumnType("integer");
        b.Property(x=>x.OriginAttemptId).HasColumnName("origin_attempt_id").HasColumnType("uuid");
        b.Property(x=>x.ActivatedAt).HasColumnName("activated_at").HasColumnType("timestamptz");
        b.Property(x=>x.DeadlineAt).HasColumnName("deadline_at").HasColumnType("timestamptz");
        b.Property(x=>x.Status).HasColumnName("status").HasColumnType("varchar(32)").IsRequired().HasDefaultValue("Active");
        b.Property(x=>x.ContextJson).HasColumnName("context_json").HasColumnType("jsonb").IsRequired().HasDefaultValue("{}");
        b.Property(x=>x.ArtifactId).HasColumnName("artifact_id").HasColumnType("uuid");
        b.Property(x=>x.AcceptanceId).HasColumnName("acceptance_id").HasColumnType("uuid");
        b.Property(x=>x.PromotionId).HasColumnName("promotion_id").HasColumnType("uuid");
        b.Property(x=>x.ActualReleaseId).HasColumnName("actual_release_id").HasColumnType("uuid");
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken().HasDefaultValue(1L);
        b.HasOne<ReleasePipelineRunStage>().WithMany().HasForeignKey(x=>new{x.RunStageId,x.RunId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.RunId,x.ProjectId,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.RunStageId});
        b.HasIndex(x=>new{x.RunStageId,x.AttemptNo}).IsUnique();
        b.HasIndex(x=>x.RunStageId).IsUnique().HasFilter("status='Active'");
        b.HasOne<ReleasePipelineStageAttempt>().WithMany().HasForeignKey(x=>new{x.OriginAttemptId,x.RunStageId}).HasPrincipalKey(x=>new{x.Id,x.RunStageId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseArtifact>().WithMany().HasForeignKey(x=>new{x.ArtifactId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseTestAcceptance>().WithMany().HasForeignKey(x=>new{x.AcceptanceId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleasePromotion>().WithMany().HasForeignKey(x=>new{x.PromotionId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.TargetEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>x.PromotionId).IsUnique().HasFilter("promotion_id IS NOT NULL");
        b.HasOne<ReleaseRecord>().WithMany().HasForeignKey(x=>new{x.ActualReleaseId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_pipeline_attempt","attempt_no>=1 AND revision>=1 AND deadline_at>activated_at AND (origin_attempt_id IS NULL OR origin_attempt_id<>id) AND status IN ('Active','Passed','Rejected','DeploymentFailed','VerificationFailed','TimedOut','Invalidated','Cancelled')"));
    }
}

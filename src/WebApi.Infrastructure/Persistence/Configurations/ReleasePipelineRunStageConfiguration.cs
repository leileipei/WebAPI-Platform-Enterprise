using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleasePipelineRunStageConfiguration : IEntityTypeConfiguration<ReleasePipelineRunStage>
{
    public void Configure(EntityTypeBuilder<ReleasePipelineRunStage> b)
    {
        b.ToTable("release_pipeline_run_stages"); b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()");
        b.Property(x=>x.RunId).HasColumnName("run_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.StageOrder).HasColumnName("stage_order").HasColumnType("integer");
        b.Property(x=>x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid");
        b.Property(x=>x.SourceStageId).HasColumnName("source_stage_id").HasColumnType("uuid");
        b.Property(x=>x.StageArtifactId).HasColumnName("stage_artifact_id").HasColumnType("uuid");
        b.Property(x=>x.CurrentAttemptId).HasColumnName("current_attempt_id").HasColumnType("uuid");
        b.Property(x=>x.ProfileJson).HasColumnName("profile_json").HasColumnType("jsonb").IsRequired().HasDefaultValue("{}");
        b.Property(x=>x.ProfileHash).HasColumnName("profile_hash").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.Status).HasColumnName("status").HasColumnType("varchar(32)").IsRequired().HasDefaultValue("Pending");
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken().HasDefaultValue(1L);
        b.HasOne<ReleasePipelineRun>().WithMany().HasForeignKey(x=>new{x.RunId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x=>new{x.EnvironmentId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.RunId});
        b.HasAlternateKey(x=>new{x.Id,x.ProjectId,x.EnvironmentId});
        b.HasAlternateKey(x=>new{x.Id,x.RunId,x.ProjectId,x.EnvironmentId});
        b.HasIndex(x=>new{x.RunId,x.StageOrder}).IsUnique();
        b.HasIndex(x=>new{x.RunId,x.EnvironmentId}).IsUnique();
        b.HasOne<ReleasePipelineRunStage>().WithMany().HasForeignKey(x=>new{x.SourceStageId,x.RunId}).HasPrincipalKey(x=>new{x.Id,x.RunId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseArtifact>().WithMany().HasForeignKey(x=>new{x.StageArtifactId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleasePipelineStageAttempt>().WithMany().HasForeignKey(x=>new{x.CurrentAttemptId,x.Id}).HasPrincipalKey(x=>new{x.Id,x.RunStageId}).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_pipeline_stage","stage_order BETWEEN 1 AND 8 AND revision>=1 AND length(profile_hash)=64 AND (source_stage_id IS NULL OR source_stage_id<>id) AND status IN ('Pending','AwaitingEvidence','AwaitingAcceptance','AwaitingMapping','AwaitingPrecheck','AwaitingApproval','ReadyToDeploy','Deploying','AwaitingVerification','Passed','Rejected','DeploymentFailed','VerificationFailed','TimedOut','Invalidated','Cancelled')"));
    }
}

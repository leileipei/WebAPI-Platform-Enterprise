using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleasePipelineRunConfiguration : IEntityTypeConfiguration<ReleasePipelineRun>
{
    public void Configure(EntityTypeBuilder<ReleasePipelineRun> b)
    {
        b.ToTable("release_pipeline_runs"); b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.PipelineVersionId).HasColumnName("pipeline_version_id").HasColumnType("uuid");
        b.Property(x=>x.DefinitionHash).HasColumnName("definition_hash").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.RootArtifactId).HasColumnName("root_artifact_id").HasColumnType("uuid");
        b.Property(x=>x.RootArtifactHash).HasColumnName("root_artifact_hash").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.SourceEnvironmentId).HasColumnName("source_environment_id").HasColumnType("uuid");
        b.Property(x=>x.SourceReleaseId).HasColumnName("source_release_id").HasColumnType("uuid");
        b.Property(x=>x.SourceConfigVersion).HasColumnName("source_config_version").HasColumnType("bigint");
        b.Property(x=>x.SourceDeploymentSequence).HasColumnName("source_deployment_sequence").HasColumnType("bigint");
        b.Property(x=>x.PolicyRevision).HasColumnName("policy_revision").HasColumnType("bigint");
        b.Property(x=>x.Status).HasColumnName("status").HasColumnType("varchar(32)").IsRequired().HasDefaultValue("Active");
        b.Property(x=>x.CurrentStageOrder).HasColumnName("current_stage_order").HasColumnType("integer").HasDefaultValue(1);
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken().HasDefaultValue(1L);
        b.Property(x=>x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        b.Property(x=>x.ProjectionCheckedAt).HasColumnName("projection_checked_at").HasColumnType("timestamptz");
        b.HasIndex(x=>new{x.Status,x.ProjectionCheckedAt,x.Id}).HasDatabaseName("ix_pipeline_projection_due");
        b.Property(x=>x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamptz");
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleasePipelineVersion>().WithMany().HasForeignKey(x=>new{x.PipelineVersionId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseArtifact>().WithMany().HasForeignKey(x=>new{x.RootArtifactId,x.ProjectId,x.SourceEnvironmentId,x.SourceReleaseId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId,x.SourceReleaseId}).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.ProjectId});
        b.HasIndex(x=>x.ProjectId).IsUnique().HasFilter("status IN ('Active','Paused','TimedOut')");
        b.HasIndex(x=>new{x.ProjectId,x.CreatedAt,x.Id}).IsDescending(false,true,true);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_pipeline_run","status IN ('Active','Paused','TimedOut','Invalidated','Cancelled','Completed') AND current_stage_order BETWEEN 1 AND 8 AND revision>=1 AND policy_revision>=1 AND source_config_version>0 AND source_deployment_sequence>0 AND length(definition_hash)=64 AND length(root_artifact_hash)=64"));
    }
}

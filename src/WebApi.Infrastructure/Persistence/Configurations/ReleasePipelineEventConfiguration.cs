using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleasePipelineEventConfiguration : IEntityTypeConfiguration<ReleasePipelineEvent>
{
    public void Configure(EntityTypeBuilder<ReleasePipelineEvent> b)
    {
        b.ToTable("release_pipeline_events"); b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()");
        b.Property(x=>x.RunId).HasColumnName("run_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.StageId).HasColumnName("stage_id").HasColumnType("uuid");
        b.Property(x=>x.AttemptId).HasColumnName("attempt_id").HasColumnType("uuid");
        b.Property(x=>x.FromStatus).HasColumnName("from_status").HasColumnType("varchar(32)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.ToStatus).HasColumnName("to_status").HasColumnType("varchar(32)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.ReasonCode).HasColumnName("reason_code").HasColumnType("varchar(128)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.ActorId).HasColumnName("actor_id").HasColumnType("uuid");
        b.Property(x=>x.RelatedId).HasColumnName("related_id").HasColumnType("uuid");
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        b.HasOne<ReleasePipelineRun>().WithMany().HasForeignKey(x=>new{x.RunId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleasePipelineRunStage>().WithMany().HasForeignKey(x=>new{x.StageId,x.RunId}).HasPrincipalKey(x=>new{x.Id,x.RunId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleasePipelineStageAttempt>().WithMany().HasForeignKey(x=>new{x.AttemptId,x.StageId}).HasPrincipalKey(x=>new{x.Id,x.RunStageId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>new{x.RunId,x.CreatedAt,x.Id});
        b.ToTable(t=>t.HasCheckConstraint("ck_pipeline_event","attempt_id IS NULL OR stage_id IS NOT NULL"));
    }
}

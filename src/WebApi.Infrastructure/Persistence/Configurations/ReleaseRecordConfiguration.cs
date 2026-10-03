using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleaseRecordConfiguration : IEntityTypeConfiguration<ReleaseRecord>
{
    public void Configure(EntityTypeBuilder<ReleaseRecord> b)
    {
        b.ToTable("release_records"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ReleaseNo).HasColumnName("release_no").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.FromConfigVersion).HasColumnName("from_config_version").HasColumnType("bigint").IsRequired();
        b.Property(x => x.ToConfigVersion).HasColumnName("to_config_version").HasColumnType("bigint").IsRequired();
        b.Property(x => x.ReleaseType).HasColumnName("release_type").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.RequestedBy).HasColumnName("requested_by").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ApprovedBy).HasColumnName("approved_by").HasColumnType("uuid");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamptz");
        b.Property(x => x.RollbackOf).HasColumnName("rollback_of").HasColumnType("uuid");
        b.Property(x => x.RecoveryOf).HasColumnName("recovery_of").HasColumnType("uuid");
        b.Property(x => x.DeploymentSequence).HasColumnName("deployment_sequence").HasColumnType("bigint").HasDefaultValue(0L);
        b.Property(x => x.BaselineConfigVersion).HasColumnName("baseline_config_version").HasColumnType("bigint").IsRequired().HasDefaultValue(0L);
        b.Property(x => x.CandidateBytes).HasColumnName("candidate_bytes").HasColumnType("bytea");
        b.Property(x => x.ApprovalPolicy).HasColumnName("approval_policy").HasColumnType("jsonb");
        b.Property(x => x.DeadlineAt).HasColumnName("deadline_at").HasColumnType("timestamptz");
        b.Property(x => x.FailureCode).HasColumnName("failure_code").HasColumnType("varchar(128)");
        b.Property(x => x.PublishRequestedBy).HasColumnName("publish_requested_by").HasColumnType("uuid");
        b.Property(x => x.PublishTraceId).HasColumnName("publish_trace_id").HasColumnType("varchar(128)");
    }
}

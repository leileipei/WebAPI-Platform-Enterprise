using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApprovalTaskConfiguration : IEntityTypeConfiguration<ApprovalTask>
{
    public void Configure(EntityTypeBuilder<ApprovalTask> b)
    {
        b.ToTable("approval_tasks"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.FlowId).HasColumnName("flow_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ReleaseId).HasColumnName("release_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.StepOrder).HasColumnName("step_order").HasColumnType("int").IsRequired();
        b.Property(x => x.AssigneeUserId).HasColumnName("assignee_user_id").HasColumnType("uuid");
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.Comment).HasColumnName("comment").HasColumnType("text");
        b.Property(x => x.ActedAt).HasColumnName("acted_at").HasColumnType("timestamptz");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApprovalStepConfiguration : IEntityTypeConfiguration<ApprovalStep>
{
    public void Configure(EntityTypeBuilder<ApprovalStep> b)
    {
        b.ToTable("approval_steps"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.FlowId).HasColumnName("flow_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.StepOrder).HasColumnName("step_order").HasColumnType("int").IsRequired();
        b.Property(x => x.RoleCode).HasColumnName("role_code").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.RequiredCount).HasColumnName("required_count").HasColumnType("int").IsRequired();
    }
}

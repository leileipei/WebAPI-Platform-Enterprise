using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("bigint").IsRequired().UseIdentityByDefaultColumn();
        b.Property(x => x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x => x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid");
        b.Property(x => x.UserId).HasColumnName("user_id").HasColumnType("uuid");
        b.Property(x => x.Action).HasColumnName("action").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.ResourceType).HasColumnName("resource_type").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.ResourceId).HasColumnName("resource_id").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb");
        b.Property(x => x.AfterJson).HasColumnName("after_json").HasColumnType("jsonb");
        b.Property(x => x.Ip).HasColumnName("ip").HasColumnType("inet");
        b.Property(x => x.TraceId).HasColumnName("trace_id").HasColumnType("varchar(64)");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
    }
}

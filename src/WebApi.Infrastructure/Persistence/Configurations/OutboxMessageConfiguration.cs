using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ReleaseId).HasColumnName("release_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.DeploymentSequence).HasColumnName("deployment_sequence").HasColumnType("bigint").IsRequired().HasDefaultValue(0L);
        b.Property(x => x.EventType).HasColumnName("event_type").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.ProcessedAt).HasColumnName("processed_at").HasColumnType("timestamptz");
        b.Property(x => x.LeaseUntil).HasColumnName("lease_until").HasColumnType("timestamptz");
        b.Property(x => x.Attempts).HasColumnName("attempts").HasColumnType("int").IsRequired().HasDefaultValue(0);
    }
}

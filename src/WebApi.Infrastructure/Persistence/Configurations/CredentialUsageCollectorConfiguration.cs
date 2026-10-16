using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class CredentialUsageCollectorConfiguration : IEntityTypeConfiguration<CredentialUsageCollector>
{
    public void Configure(EntityTypeBuilder<CredentialUsageCollector> b)
    {
        b.ToTable("credential_usage_collectors",t=>{t.HasCheckConstraint("ck_usage_collector_protocol","protocol = '1.0'");t.HasCheckConstraint("ck_usage_collector_sequence","accepted_sequence >= 0");});
        b.HasKey(x=>new{x.NodeId,x.CollectorId});
        b.Property(x=>x.NodeId).HasColumnName("node_id");b.Property(x=>x.CollectorId).HasColumnName("collector_id");b.Property(x=>x.EnvironmentId).HasColumnName("environment_id");
        b.Property(x=>x.Protocol).HasColumnName("protocol").HasColumnType("varchar(8)").HasDefaultValue("1.0");
        b.Property(x=>x.AcceptedSequence).HasColumnName("accepted_sequence").HasColumnType("bigint").HasDefaultValue(0L);
        b.Property(x=>x.AcceptedThrough).HasColumnName("accepted_through").HasColumnType("timestamptz");b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("NOW()");b.Property(x=>x.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
        b.HasOne<GatewayNode>().WithMany().HasForeignKey(x=>x.NodeId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x=>x.ClosedAt).HasDatabaseName("ix_usage_collectors_closed");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class CredentialUsageCollectorInstanceConfiguration : IEntityTypeConfiguration<CredentialUsageCollectorInstance>
{
    public void Configure(EntityTypeBuilder<CredentialUsageCollectorInstance> b)
    {
        b.ToTable("credential_usage_collector_instances");b.HasKey(x=>new{x.NodeId,x.CollectorId,x.InstanceId});
        b.Property(x=>x.NodeId).HasColumnName("node_id");b.Property(x=>x.CollectorId).HasColumnName("collector_id");b.Property(x=>x.InstanceId).HasColumnName("instance_id");
        b.Property(x=>x.RegisteredAt).HasColumnName("registered_at").HasColumnType("timestamptz").HasDefaultValueSql("NOW()");b.Property(x=>x.EndedAt).HasColumnName("ended_at").HasColumnType("timestamptz");
        b.Property(x=>x.IdentityProof).HasColumnName("identity_proof").HasColumnType("varchar(32)").HasDefaultValue("current");
        b.HasOne<CredentialUsageCollector>().WithMany().HasForeignKey(x=>new{x.NodeId,x.CollectorId}).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x=>x.EndedAt).HasDatabaseName("ix_usage_instances_ended");
    }
}

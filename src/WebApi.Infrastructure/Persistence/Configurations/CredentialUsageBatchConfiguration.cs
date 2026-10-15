using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class CredentialUsageBatchConfiguration : IEntityTypeConfiguration<CredentialUsageBatch>
{
    public void Configure(EntityTypeBuilder<CredentialUsageBatch> b)
    {
        b.ToTable("credential_usage_batches",t=>{t.HasCheckConstraint("ck_usage_batch_sequence","sequence > 0");t.HasCheckConstraint("ck_usage_batch_hash","core_hash ~ '^[0-9a-f]{64}$'");});b.HasKey(x=>new{x.NodeId,x.CollectorId,x.Sequence});
        b.Property(x=>x.NodeId).HasColumnName("node_id");b.Property(x=>x.CollectorId).HasColumnName("collector_id");b.Property(x=>x.Sequence).HasColumnName("sequence").HasColumnType("bigint").ValueGeneratedNever();
        b.Property(x=>x.CoreHash).HasColumnName("core_hash").HasColumnType("char(64)").IsRequired();b.Property(x=>x.AppliedAt).HasColumnName("applied_at").HasColumnType("timestamptz").HasDefaultValueSql("NOW()");b.Property(x=>x.AcceptedThrough).HasColumnName("accepted_through").HasColumnType("timestamptz");
        b.HasOne<CredentialUsageCollector>().WithMany().HasForeignKey(x=>new{x.NodeId,x.CollectorId}).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x=>x.AppliedAt).HasDatabaseName("ix_usage_batches_applied");
    }
}

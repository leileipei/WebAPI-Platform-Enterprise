using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Persistence.Configurations;

public sealed class EnvironmentSecuritySourceReceiptConfiguration : IEntityTypeConfiguration<EnvironmentSecuritySourceReceipt>
{
    public void Configure(EntityTypeBuilder<EnvironmentSecuritySourceReceipt> b)
    {
        b.ToTable("environment_security_source_receipts");
        b.HasKey(x => x.EnvironmentId);
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id");
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.OrganizationId).HasColumnName("organization_id");
        b.Property(x => x.SourceDatabase).HasColumnName("source_database").HasColumnType("varchar(63)").IsRequired();
        b.Property(x => x.SourceDatabaseOid).HasColumnName("source_database_oid").IsRequired();
        b.Property(x => x.SourceClusterId).HasColumnName("source_cluster_id").HasColumnType("varchar(20)").IsRequired();
        b.Property(x => x.SourceMigration).HasColumnName("source_migration").HasColumnType("varchar(150)").IsRequired();
        b.Property(x => x.SourceSchemaHash).HasColumnName("source_schema_hash").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.SourceHistoryHash).HasColumnName("source_history_hash").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.CapturedAt).HasColumnName("captured_at").IsRequired();
        b.HasOne<EnvironmentSecurityInitialization>().WithMany()
            .HasForeignKey(x => new { x.EnvironmentId, x.ProjectId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.EnvironmentId, x.ProjectId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("ck_environment_security_source_receipt", """
            source_database_oid > 0 AND length(source_database) > 0 AND source_cluster_id ~ '^[0-9]{1,20}$'
            AND length(source_migration) > 0 AND source_schema_hash ~ '^[a-f0-9]{64}$' AND source_history_hash ~ '^[a-f0-9]{64}$'
            """));
    }
}

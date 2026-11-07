using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Persistence.Configurations;

public sealed class EnvironmentSecurityInitializationConfiguration : IEntityTypeConfiguration<EnvironmentSecurityInitialization>
{
    public void Configure(EntityTypeBuilder<EnvironmentSecurityInitialization> b)
    {
        b.ToTable("environment_security_initializations");
        b.HasKey(x => x.EnvironmentId);
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id");
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.OrganizationId).HasColumnName("organization_id");
        b.Property(x => x.State).HasColumnName("state").HasColumnType("varchar(32)").HasDefaultValue("Unknown").IsRequired();
        b.Property(x => x.EvidenceHash).HasColumnName("evidence_hash").HasColumnType("varchar(64)");
        b.Property(x => x.FreshProofId).HasColumnName("fresh_proof_id");
        b.Property(x => x.ConfirmedBy).HasColumnName("confirmed_by");
        b.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at");
        b.Property(x => x.ConfirmationReason).HasColumnName("confirmation_reason").HasColumnType("varchar(1000)");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").HasDefaultValue(1L).IsConcurrencyToken().IsRequired();
        b.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x => new { x.EnvironmentId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x => x.ConfirmedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("ck_environment_security_initialization", """
            revision > 0 AND (
              (state = 'Unknown' AND evidence_hash IS NULL AND fresh_proof_id IS NULL AND confirmed_by IS NULL AND confirmed_at IS NULL AND confirmation_reason IS NULL)
              OR (state = 'FreshNonStrict' AND fresh_proof_id IS NOT NULL AND evidence_hash IS NULL AND confirmed_by IS NULL AND confirmed_at IS NULL AND confirmation_reason IS NULL)
              OR (state = 'VerifiedLegacyNetwork' AND evidence_hash IS NOT NULL AND evidence_hash ~ '^[a-f0-9]{64}$' AND fresh_proof_id IS NULL AND confirmed_by IS NOT NULL AND confirmed_at IS NOT NULL AND confirmation_reason IS NOT NULL AND length(trim(confirmation_reason)) BETWEEN 1 AND 1000)
              OR (state = 'Governed' AND (evidence_hash IS NULL OR evidence_hash ~ '^[a-f0-9]{64}$') AND ((confirmed_by IS NULL AND confirmed_at IS NULL AND confirmation_reason IS NULL) OR (confirmed_by IS NOT NULL AND confirmed_at IS NOT NULL AND confirmation_reason IS NOT NULL AND length(trim(confirmation_reason)) BETWEEN 1 AND 1000)))
            )
            """));
    }
}

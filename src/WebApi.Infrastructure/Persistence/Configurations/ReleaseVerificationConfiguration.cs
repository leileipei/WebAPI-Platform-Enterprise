using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleaseVerificationConfiguration : IEntityTypeConfiguration<ReleaseVerification>
{
    public void Configure(EntityTypeBuilder<ReleaseVerification> b)
    {
        b.ToTable("release_verifications");b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.ArtifactId).HasColumnName("artifact_id").HasColumnType("uuid");
        b.Property(x=>x.PromotionId).HasColumnName("promotion_id").HasColumnType("uuid");
        b.Property(x=>x.ReleaseId).HasColumnName("release_id").HasColumnType("uuid");
        b.Property(x=>x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid");
        b.Property(x=>x.ConfigVersion).HasColumnName("config_version").HasColumnType("bigint");
        b.Property(x=>x.DeploymentSequence).HasColumnName("deployment_sequence").HasColumnType("bigint");
        b.Property(x=>x.SnapshotHash).HasColumnName("snapshot_hash").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.AccessAddressRevision).HasColumnName("access_address_revision").HasColumnType("bigint");
        b.Property(x=>x.AccessContextJson).HasColumnName("access_context_json").HasColumnType("jsonb").IsRequired().HasDefaultValue("{}");
        b.Property(x=>x.PolicyRevision).HasColumnName("policy_revision").HasColumnType("bigint");
        b.Property(x=>x.Phase).HasColumnName("phase").HasColumnType("varchar(32)").IsRequired().HasDefaultValue("SourceTest");
        b.Property(x=>x.Type).HasColumnName("type").HasColumnType("varchar(32)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.Result).HasColumnName("result").HasColumnType("varchar(16)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.IsManual).HasColumnName("is_manual").HasColumnType("boolean").HasDefaultValue(true);
        b.Property(x=>x.ReportId).HasColumnName("report_id").HasColumnType("uuid");
        b.Property(x=>x.ReportHash).HasColumnName("report_hash").HasColumnType("varchar(64)");
        b.Property(x=>x.Comment).HasColumnName("comment").HasColumnType("text").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
        b.Property(x=>x.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamptz");
        b.Property(x=>x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
        b.Property(x=>x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.ProjectId,x.EnvironmentId});
        b.HasIndex(x=>new{x.ArtifactId,x.Phase,x.Type,x.CreatedAt}).IsDescending(false,false,false,true);
        b.HasOne<ReleaseArtifact>().WithMany().HasForeignKey(x=>new{x.ArtifactId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleasePromotion>().WithMany().HasForeignKey(x=>new{x.PromotionId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.TargetEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseRecord>().WithMany().HasForeignKey(x=>new{x.ReleaseId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VerificationReport>().WithMany().HasForeignKey(x=>new{x.ReportId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_verification_fact", "phase IN ('SourceTest','Production') AND result IN ('Passed','Failed') AND is_manual AND config_version > 0 AND deployment_sequence > 0 AND access_address_revision >= 1 AND started_at <= finished_at AND finished_at <= created_at AND expires_at > finished_at AND ((phase='SourceTest' AND promotion_id IS NULL AND type IN ('InterfaceFunction','Integration','ContractCompatibility')) OR (phase='Production' AND promotion_id IS NOT NULL AND type IN ('EntryConnectivity','AuthenticationAuthorization','CriticalBusinessCall')))"));
    }
}

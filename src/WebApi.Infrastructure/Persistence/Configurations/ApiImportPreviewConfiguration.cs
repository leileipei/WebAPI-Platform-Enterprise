using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiImportPreviewConfiguration : IEntityTypeConfiguration<ApiImportPreview>
{
    public void Configure(EntityTypeBuilder<ApiImportPreview> b)
    {
        b.ToTable("api_import_previews", t => { t.HasCheckConstraint("ck_import_preview_status", "status IN ('Active','Committed','Revoked','Expired')"); t.HasCheckConstraint("ck_import_preview_revision", "revision >= 1 AND source_policy_revision >= 0"); t.HasCheckConstraint("ck_import_preview_expiry", "expires_at > created_at"); t.HasCheckConstraint("ck_import_preview_dialect", "dialect IN ('Oas30','Oas31') AND source_format IN ('json','yaml')"); }); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id"); b.Property(x => x.OrganizationId).HasColumnName("organization_id"); b.Property(x => x.ProjectId).HasColumnName("project_id"); b.Property(x => x.EnvironmentId).HasColumnName("environment_id"); b.Property(x => x.ClusterId).HasColumnName("cluster_id"); b.Property(x => x.ActorId).HasColumnName("actor_id");
        b.Property(x => x.SourcePolicyRevision).HasColumnName("source_policy_revision"); b.Property(x => x.BundleJson).HasColumnName("bundle_json").HasColumnType("jsonb"); b.Property(x => x.PreviewJson).HasColumnName("preview_json").HasColumnType("jsonb"); b.Property(x => x.SourceHash).HasColumnName("source_hash").HasColumnType("varchar(64)").IsRequired(); b.Property(x => x.BundleHash).HasColumnName("bundle_hash").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.SourceFormat).HasColumnName("source_format").HasColumnType("varchar(16)").IsRequired(); b.Property(x => x.Dialect).HasColumnName("dialect").HasColumnType("varchar(16)").IsRequired(); b.Property(x => x.CreatedAt).HasColumnName("created_at"); b.Property(x => x.ExpiresAt).HasColumnName("expires_at"); b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(16)").IsRequired(); b.Property(x => x.ImportId).HasColumnName("import_id"); b.Property(x => x.CommittedTargetsJson).HasColumnName("committed_targets_json").HasColumnType("jsonb"); b.Property(x => x.ReceiptExpiresAt).HasColumnName("receipt_expires_at"); b.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken();
        b.HasIndex(x => new { x.ProjectId, x.ActorId, x.Status, x.ExpiresAt }); b.HasIndex(x => new { x.Status, x.ExpiresAt }); b.HasIndex(x => x.ReceiptExpiresAt);
        b.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.OrganizationId }).HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x => new { x.EnvironmentId, x.ProjectId }).HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UpstreamCluster>().WithMany().HasForeignKey(x => new { x.ClusterId, x.EnvironmentId }).HasPrincipalKey(x => new { x.Id, x.EnvironmentId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}

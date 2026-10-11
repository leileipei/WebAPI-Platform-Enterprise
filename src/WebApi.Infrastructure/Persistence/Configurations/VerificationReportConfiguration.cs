using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class VerificationReportConfiguration : IEntityTypeConfiguration<VerificationReport>
{
    public void Configure(EntityTypeBuilder<VerificationReport> b)
    {
        b.ToTable("verification_reports");b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid");
        b.Property(x=>x.ArtifactId).HasColumnName("artifact_id").HasColumnType("uuid");
        b.Property(x=>x.PromotionId).HasColumnName("promotion_id").HasColumnType("uuid");
        b.Property(x=>x.StorageKey).HasColumnName("storage_key").HasColumnType("varchar(128)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.ContentType).HasColumnName("content_type").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint");
        b.Property(x=>x.Sha256).HasColumnName("sha256").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.ProjectId,x.EnvironmentId});b.HasIndex(x=>x.StorageKey).IsUnique();
        b.HasOne<ReleaseArtifact>().WithMany().HasForeignKey(x=>new{x.ArtifactId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleasePromotion>().WithMany().HasForeignKey(x=>new{x.PromotionId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.TargetEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_report", "size_bytes BETWEEN 1 AND 10485760 AND content_type IN ('application/pdf','text/plain') AND (artifact_id IS NOT NULL)::int + (promotion_id IS NOT NULL)::int = 1"));
    }
}

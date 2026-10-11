using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleaseArtifactConfiguration : IEntityTypeConfiguration<ReleaseArtifact>
{
    public void Configure(EntityTypeBuilder<ReleaseArtifact> b)
    {
        b.ToTable("release_artifacts");b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.SourceEnvironmentId).HasColumnName("source_environment_id").HasColumnType("uuid");
        b.Property(x=>x.SourceReleaseId).HasColumnName("source_release_id").HasColumnType("uuid");
        b.Property(x=>x.CanonicalContent).HasColumnName("canonical_content").HasColumnType("jsonb").IsRequired().HasDefaultValue("{}");
        b.Property(x=>x.ArtifactHash).HasColumnName("artifact_hash").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.SourceSnapshotHash).HasColumnName("source_snapshot_hash").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.ProjectId});b.HasAlternateKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId});b.HasAlternateKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId,x.SourceReleaseId});
        b.HasIndex(x=>new{x.SourceReleaseId,x.ArtifactHash}).IsUnique();
        b.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x=>new{x.SourceEnvironmentId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseRecord>().WithMany().HasForeignKey(x=>new{x.SourceReleaseId,x.SourceEnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
    }
}

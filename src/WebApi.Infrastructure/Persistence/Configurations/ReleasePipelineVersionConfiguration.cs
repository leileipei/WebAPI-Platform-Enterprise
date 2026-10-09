using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleasePipelineVersionConfiguration : IEntityTypeConfiguration<ReleasePipelineVersion>
{
    public void Configure(EntityTypeBuilder<ReleasePipelineVersion> b)
    {
        b.ToTable("release_pipeline_versions"); b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()");
        b.Property(x=>x.PipelineId).HasColumnName("pipeline_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.VersionNo).HasColumnName("version_no").HasColumnType("integer");
        b.Property(x=>x.ContentJson).HasColumnName("content_json").HasColumnType("jsonb").IsRequired().HasDefaultValue("{}");
        b.Property(x=>x.DefinitionHash).HasColumnName("definition_hash").HasColumnType("varchar(64)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        b.HasOne<ReleasePipeline>().WithMany().HasForeignKey(x=>new{x.PipelineId,x.ProjectId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId}).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.ProjectId});
        b.HasIndex(x=>new{x.PipelineId,x.VersionNo}).IsUnique();
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_pipeline_version","version_no>=1 AND length(definition_hash)=64"));
    }
}

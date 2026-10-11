using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleasePipelineConfiguration : IEntityTypeConfiguration<ReleasePipeline>
{
    public void Configure(EntityTypeBuilder<ReleasePipeline> b)
    {
        b.ToTable("release_pipelines"); b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.Name).HasColumnName("name").HasColumnType("varchar(100)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.Description).HasColumnName("description").HasColumnType("text").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.DraftJson).HasColumnName("draft_json").HasColumnType("jsonb").IsRequired().HasDefaultValue("{}");
        b.Property(x=>x.Status).HasColumnName("status").HasColumnType("varchar(32)").IsRequired().HasDefaultValue("Active");
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken().HasDefaultValue(1L);
        b.Property(x=>x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        b.Property(x=>x.UpdatedBy).HasColumnName("updated_by").HasColumnType("uuid");
        b.Property(x=>x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x=>new{x.Id,x.ProjectId});
        b.HasIndex(x=>new{x.ProjectId,x.CreatedAt,x.Id}).IsDescending(false,true,true);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_pipeline_draft","length(btrim(name)) BETWEEN 1 AND 100 AND revision>=1 AND status IN ('Active','Archived')"));
    }
}

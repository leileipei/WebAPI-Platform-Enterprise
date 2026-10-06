using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiVersionComparisonConfiguration:IEntityTypeConfiguration<ApiVersionComparison>
{
    public void Configure(EntityTypeBuilder<ApiVersionComparison> b)
    {
        ComparisonColumns.Map(b,"api_version_comparisons");b.Property(x=>x.CountsJson).HasColumnType("jsonb");b.Property(x=>x.Coverage).HasMaxLength(16);b.Property(x=>x.EngineVersion).HasMaxLength(64);b.Property(x=>x.FromVersion).HasMaxLength(32);b.Property(x=>x.ToVersion).HasMaxLength(32);
        b.HasIndex(x=>new{x.ApiId,x.CreatedAt,x.Id});
        b.ToTable(t=>{t.HasCheckConstraint("ck_api_version_comparisons_coverage","coverage IN ('Complete','Limited','Invalid')");t.HasCheckConstraint("ck_api_version_comparisons_versions","from_version_id <> to_version_id AND from_revision >= 1 AND to_revision >= 1");});
    }
}

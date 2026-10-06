using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiVersionRiskReviewConfiguration:IEntityTypeConfiguration<ApiVersionRiskReview>
{
    public void Configure(EntityTypeBuilder<ApiVersionRiskReview> b)
    {
        ComparisonColumns.Map(b,"api_version_risk_reviews");b.Property(x=>x.Decision).HasMaxLength(24);b.Property(x=>x.Comment).HasMaxLength(2000);b.HasIndex(x=>new{x.ComparisonId,x.CreatedAt,x.Id});b.HasOne<ApiVersionComparison>().WithMany().HasForeignKey(x=>x.ComparisonId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_api_version_risk_reviews_decision","decision IN ('Reviewed','AcceptedRisk') AND (decision <> 'AcceptedRisk' OR (comment IS NOT NULL AND length(btrim(comment)) BETWEEN 10 AND 2000))"));
    }
}

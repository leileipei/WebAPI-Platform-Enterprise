using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleasePromotionMappingConfiguration : IEntityTypeConfiguration<ReleasePromotionMapping>
{
    public void Configure(EntityTypeBuilder<ReleasePromotionMapping> b)
    {
        b.ToTable("release_promotion_mappings");b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.PromotionId).HasColumnName("promotion_id").HasColumnType("uuid");
        b.Property(x=>x.TargetEnvironmentId).HasColumnName("target_environment_id").HasColumnType("uuid");
        b.Property(x=>x.ResourceKey).HasColumnName("resource_key").HasColumnType("varchar(512)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.Kind).HasColumnName("kind").HasColumnType("varchar(32)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.TargetRouteId).HasColumnName("target_route_id").HasColumnType("uuid");
        b.Property(x=>x.ClusterId).HasColumnName("cluster_id").HasColumnType("uuid");
        b.Property(x=>x.ApplicationId).HasColumnName("application_id").HasColumnType("uuid");
        b.Property(x=>x.ParametersJson).HasColumnName("parameters_json").HasColumnType("jsonb").IsRequired().HasDefaultValue("{}");
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken().HasDefaultValue(1L);
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>new{x.PromotionId,x.Kind,x.ResourceKey}).IsUnique();
        b.HasOne<ReleasePromotion>().WithMany().HasForeignKey(x=>new{x.PromotionId,x.ProjectId,x.TargetEnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.TargetEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApiRoute>().WithMany().HasForeignKey(x=>new{x.TargetRouteId,x.TargetEnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UpstreamCluster>().WithMany().HasForeignKey(x=>new{x.ClusterId,x.TargetEnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationRecord>().WithMany().HasForeignKey(x=>new{x.ApplicationId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
    }
}

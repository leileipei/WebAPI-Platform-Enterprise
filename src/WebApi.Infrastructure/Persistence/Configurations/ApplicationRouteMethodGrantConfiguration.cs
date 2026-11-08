using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApplicationRouteMethodGrantConfiguration:IEntityTypeConfiguration<ApplicationRouteMethodGrant>
{
    public void Configure(EntityTypeBuilder<ApplicationRouteMethodGrant> b)
    {
        b.ToTable("application_route_method_grants",t=>t.HasCheckConstraint("ck_route_method_grant","authorization_revision>0 AND method IN ('GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS')"));
        b.HasKey(x=>new{x.PermissionId,x.RouteId,x.Method});
        b.Property(x=>x.PermissionId).HasColumnName("permission_id");b.Property(x=>x.EnvironmentId).HasColumnName("environment_id");b.Property(x=>x.ApiId).HasColumnName("api_id");b.Property(x=>x.RouteId).HasColumnName("route_id");b.Property(x=>x.AuthorizationRevision).HasColumnName("authorization_revision");b.Property(x=>x.Method).HasColumnName("method").HasMaxLength(8);
        b.HasOne<ApplicationApiPermission>().WithMany().HasForeignKey(x=>new{x.PermissionId,x.EnvironmentId,x.ApiId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId,x.ApiId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApiRoute>().WithMany().HasForeignKey(x=>new{x.RouteId,x.EnvironmentId,x.ApiId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId,x.ApiId}).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>x.RouteId);
    }
}

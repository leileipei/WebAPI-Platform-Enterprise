using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class RouteMethodConfiguration : IEntityTypeConfiguration<RouteMethod>
{
    public void Configure(EntityTypeBuilder<RouteMethod> b)
    {
        b.ToTable("route_methods"); b.HasKey(x => new { x.RouteId, x.Method });
        b.Property(x => x.RouteId).HasColumnName("route_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Method).HasColumnName("method").HasColumnType("varchar(16)").IsRequired();
        b.Property(x => x.NormalizedPath).HasColumnName("normalized_path").HasColumnType("varchar(512)").IsRequired();
    }
}

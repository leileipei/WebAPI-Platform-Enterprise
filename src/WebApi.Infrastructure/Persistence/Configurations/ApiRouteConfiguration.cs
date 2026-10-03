using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiRouteConfiguration : IEntityTypeConfiguration<ApiRoute>
{
    public void Configure(EntityTypeBuilder<ApiRoute> b)
    {
        b.ToTable("api_routes"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ApiVersionId).HasColumnName("api_version_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.RouteName).HasColumnName("route_name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.Path).HasColumnName("path").HasColumnType("varchar(512)").IsRequired();
        b.Property(x => x.NormalizedPath).HasColumnName("normalized_path").HasColumnType("varchar(512)").IsRequired();
        b.Property(x => x.Methods).HasColumnName("methods").HasColumnType("varchar[]").IsRequired();
        b.Property(x => x.ClusterId).HasColumnName("cluster_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Priority).HasColumnName("priority").HasColumnType("int").IsRequired();
        b.Property(x => x.Enabled).HasColumnName("enabled").HasColumnType("boolean").IsRequired().HasDefaultValue(true);
        b.Property(x => x.TimeoutMs).HasColumnName("timeout_ms").HasColumnType("int").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

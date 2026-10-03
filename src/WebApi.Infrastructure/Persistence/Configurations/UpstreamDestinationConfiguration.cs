using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class UpstreamDestinationConfiguration : IEntityTypeConfiguration<UpstreamDestination>
{
    public void Configure(EntityTypeBuilder<UpstreamDestination> b)
    {
        b.ToTable("upstream_destinations"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ClusterId).HasColumnName("cluster_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.Address).HasColumnName("address").HasColumnType("varchar(1024)").IsRequired();
        b.Property(x => x.Weight).HasColumnName("weight").HasColumnType("int").IsRequired();
        b.Property(x => x.Enabled).HasColumnName("enabled").HasColumnType("boolean").IsRequired().HasDefaultValue(true);
        b.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

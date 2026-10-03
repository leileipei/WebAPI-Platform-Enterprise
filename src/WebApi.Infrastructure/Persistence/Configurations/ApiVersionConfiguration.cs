using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiVersionConfiguration : IEntityTypeConfiguration<ApiVersion>
{
    public void Configure(EntityTypeBuilder<ApiVersion> b)
    {
        b.ToTable("api_versions"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ApiId).HasColumnName("api_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Version).HasColumnName("version").HasColumnType("varchar(32)").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.OpenapiDocument).HasColumnName("openapi_document").HasColumnType("jsonb");
        b.Property(x => x.SchemaHash).HasColumnName("schema_hash").HasColumnType("varchar(128)");
        b.Property(x => x.ChangeType).HasColumnName("change_type").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
        b.Property(x => x.OpenapiSource).HasColumnName("openapi_source").HasColumnType("text");
        b.Property(x => x.SourceFormat).HasColumnName("source_format").HasColumnType("varchar(16)");
        b.Property(x => x.SealedAt).HasColumnName("sealed_at").HasColumnType("timestamptz");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiSchemaConfiguration : IEntityTypeConfiguration<ApiSchema>
{
    public void Configure(EntityTypeBuilder<ApiSchema> b)
    {
        b.ToTable("api_schemas"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ApiVersionId).HasColumnName("api_version_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.SchemaType).HasColumnName("schema_type").HasColumnType("varchar(32)").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.StatusCode).HasColumnName("status_code").HasColumnType("int");
        b.Property(x => x.ContentType).HasColumnName("content_type").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.SchemaJson).HasColumnName("schema_json").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.SchemaHash).HasColumnName("schema_hash").HasColumnType("varchar(128)");
        b.Property(x => x.ExampleJson).HasColumnName("example_json").HasColumnType("jsonb");
    }
}

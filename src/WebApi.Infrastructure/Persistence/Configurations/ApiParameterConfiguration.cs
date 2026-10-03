using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiParameterConfiguration : IEntityTypeConfiguration<ApiParameter>
{
    public void Configure(EntityTypeBuilder<ApiParameter> b)
    {
        b.ToTable("api_parameters"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ApiVersionId).HasColumnName("api_version_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Location).HasColumnName("location").HasColumnType("varchar(16)").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.DataType).HasColumnName("data_type").HasColumnType("varchar(32)").IsRequired();
        b.Property(x => x.Required).HasColumnName("required").HasColumnType("boolean").IsRequired();
        b.Property(x => x.Schema).HasColumnName("schema").HasColumnType("jsonb");
        b.Property(x => x.Description).HasColumnName("description").HasColumnType("text");
        b.Property(x => x.ExampleJson).HasColumnName("example_json").HasColumnType("jsonb");
    }
}

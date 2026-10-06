using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApiVersionContractSourcesConfiguration : IEntityTypeConfiguration<ApiVersionContractSources>
{
    public void Configure(EntityTypeBuilder<ApiVersionContractSources> b)
    {
        b.ToTable("api_version_contract_sources", t => { t.HasCheckConstraint("ck_contract_source_dialect", "dialect IN ('Oas30','Oas31')"); t.HasCheckConstraint("ck_contract_source_policy_revision", "source_policy_revision >= 0"); }); b.HasKey(x => x.ApiVersionId);
        b.Property(x => x.ApiVersionId).HasColumnName("api_version_id"); b.Property(x => x.BundleJson).HasColumnName("bundle_json").HasColumnType("jsonb").IsRequired(); b.Property(x => x.BundleHash).HasColumnName("bundle_hash").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.SourcesJson).HasColumnName("sources_json").HasColumnType("jsonb").IsRequired(); b.Property(x => x.Dialect).HasColumnName("dialect").HasColumnType("varchar(16)").IsRequired(); b.Property(x => x.SourcePolicyRevision).HasColumnName("source_policy_revision");
        b.HasOne<ApiVersion>().WithMany().HasForeignKey(x => x.ApiVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

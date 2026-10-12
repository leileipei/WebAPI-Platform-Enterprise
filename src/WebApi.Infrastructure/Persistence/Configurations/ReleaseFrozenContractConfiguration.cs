using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;

public sealed class ReleaseFrozenContractConfiguration : IEntityTypeConfiguration<ReleaseFrozenContract>
{
    public void Configure(EntityTypeBuilder<ReleaseFrozenContract> b)
    {
        b.ToTable("release_frozen_contracts",t=>
        {
            t.HasCheckConstraint("ck_frozen_contract_version","config_version > 0 AND ordinal > 0");
            t.HasCheckConstraint("ck_frozen_contract_identity","COALESCE(jsonb_typeof(frozen_json->'api')='object' AND jsonb_typeof(frozen_json->'version')='object' AND jsonb_typeof(frozen_json->'parameters')='array' AND jsonb_typeof(frozen_json->'schemas')='array' AND frozen_json->'api'->>'id'=api_id::text AND frozen_json->'version'->>'id'=api_version_id::text AND frozen_json->'version'->>'apiId'=api_id::text, false)");
        });
        b.HasKey(x=>new{x.ReleaseId,x.Ordinal});
        b.Property(x=>x.ReleaseId).HasColumnName("release_id").ValueGeneratedNever();
        b.Property(x=>x.Ordinal).HasColumnName("ordinal");
        b.Property(x=>x.EnvironmentId).HasColumnName("environment_id");
        b.Property(x=>x.ConfigVersion).HasColumnName("config_version");
        b.Property(x=>x.ApiId).HasColumnName("api_id");
        b.Property(x=>x.ApiVersionId).HasColumnName("api_version_id");
        b.Property(x=>x.FrozenJson).HasColumnName("frozen_json").HasColumnType("jsonb").IsRequired();
        b.HasOne<ReleaseRecord>().WithMany().HasForeignKey(x=>new{x.ReleaseId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>new{x.EnvironmentId,x.ApiId,x.ApiVersionId,x.ConfigVersion,x.ReleaseId,x.Ordinal}).IsDescending(false,false,false,true,false,false).HasDatabaseName("ix_frozen_contract_lookup");
    }
}

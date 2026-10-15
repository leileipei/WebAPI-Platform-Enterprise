using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class CredentialUsageCoverageConfiguration : IEntityTypeConfiguration<CredentialUsageCoverage>
{
    public void Configure(EntityTypeBuilder<CredentialUsageCoverage> b)
    {
        b.ToTable("credential_usage_coverage",t=>{t.HasCheckConstraint("ck_usage_coverage_hour","hour_start = date_trunc('hour', hour_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'");t.HasCheckConstraint("ck_usage_coverage_arrays","jsonb_typeof(intervals)='array' AND jsonb_typeof(gaps)='array'");t.HasCheckConstraint("ck_usage_coverage_budget","octet_length(intervals::text)+octet_length(gaps::text)<=1048576");t.HasCheckConstraint("ck_usage_coverage_state","state IN ('Available','NoData','Partial','Unknown','Unavailable')");});b.HasKey(x=>new{x.EnvironmentId,x.NodeId,x.HourStart});
        b.Property(x=>x.EnvironmentId).HasColumnName("environment_id");b.Property(x=>x.NodeId).HasColumnName("node_id");b.Property(x=>x.HourStart).HasColumnName("hour_start").HasColumnType("timestamptz");b.Property(x=>x.Intervals).HasColumnName("intervals").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");b.Property(x=>x.Gaps).HasColumnName("gaps").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        b.Property(x=>x.DurableThrough).HasColumnName("durable_through").HasColumnType("timestamptz");b.Property(x=>x.AcceptedThrough).HasColumnName("accepted_through").HasColumnType("timestamptz");b.Property(x=>x.State).HasColumnName("state").HasColumnType("varchar(16)").HasDefaultValue("Partial");b.Property(x=>x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").HasDefaultValueSql("NOW()");
        // Retain historical per-node evidence even if a node is explicitly deleted later.
        b.HasIndex(x=>x.HourStart).HasDatabaseName("ix_usage_coverage_retention");
    }
}

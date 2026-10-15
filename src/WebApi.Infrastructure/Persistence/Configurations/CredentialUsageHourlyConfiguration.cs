using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class CredentialUsageHourlyConfiguration : IEntityTypeConfiguration<CredentialUsageHourly>
{
    public void Configure(EntityTypeBuilder<CredentialUsageHourly> b)
    {
        b.ToTable("credential_usage_hourly",t=>{t.HasCheckConstraint("ck_usage_hourly_count","count > 0");t.HasCheckConstraint("ck_usage_hourly_hour","hour_start = date_trunc('hour', hour_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'");t.HasCheckConstraint("ck_usage_hourly_last_used","last_used_at >= hour_start AND last_used_at < hour_start + INTERVAL '1 hour'");});b.HasKey(x=>new{x.EnvironmentId,x.CredentialId,x.HourStart});
        b.Property(x=>x.EnvironmentId).HasColumnName("environment_id");b.Property(x=>x.CredentialId).HasColumnName("credential_id");b.Property(x=>x.HourStart).HasColumnName("hour_start").HasColumnType("timestamptz");b.Property(x=>x.ApplicationId).HasColumnName("application_id");b.Property(x=>x.Count).HasColumnName("count").HasColumnType("bigint");b.Property(x=>x.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
        // Published identities may outlive mutable management records. Proof is checked at ingestion.
        b.HasIndex(x=>new{x.ApplicationId,x.HourStart,x.EnvironmentId}).HasDatabaseName("ix_usage_hourly_application_window");b.HasIndex(x=>x.HourStart).HasDatabaseName("ix_usage_hourly_retention");
    }
}

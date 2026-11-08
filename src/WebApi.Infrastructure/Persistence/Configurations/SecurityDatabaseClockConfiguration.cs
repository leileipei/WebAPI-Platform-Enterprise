using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Persistence.Configurations;

public sealed class SecurityDatabaseClockConfiguration : IEntityTypeConfiguration<SecurityDatabaseClockWatermark>
{
    public void Configure(EntityTypeBuilder<SecurityDatabaseClockWatermark> builder)
    {
        builder.ToTable("security_database_clock_watermark", t => t.HasCheckConstraint("ck_security_clock_singleton", "singleton"));
        builder.HasKey(x => x.Singleton);
        builder.Property(x => x.Singleton).HasColumnName("singleton").ValueGeneratedNever();
        builder.Property(x => x.ObservedAt).HasColumnName("observed_at");
    }
}

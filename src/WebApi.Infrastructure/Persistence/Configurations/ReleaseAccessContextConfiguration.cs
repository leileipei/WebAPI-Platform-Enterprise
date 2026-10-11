using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleaseAccessContextConfiguration : IEntityTypeConfiguration<ReleaseAccessContext>
{
    public void Configure(EntityTypeBuilder<ReleaseAccessContext> b)
    {
        b.ToTable("release_access_contexts");b.HasKey(x=>x.ReleaseId);
        b.Property(x=>x.ReleaseId).HasColumnName("release_id").HasColumnType("uuid").ValueGeneratedNever();
        b.Property(x=>x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid");
        b.Property(x=>x.AccessAddressRevision).HasColumnName("access_address_revision").HasColumnType("bigint");
        b.Property(x=>x.PublicOrigin).HasColumnName("public_origin").HasColumnType("varchar(2048)");
        b.Property(x=>x.InternalOrigin).HasColumnName("internal_origin").HasColumnType("varchar(2048)");
        b.Property(x=>x.BasePath).HasColumnName("base_path").HasColumnType("varchar(512)").IsRequired();
        b.Property(x=>x.CapturedAt).HasColumnName("captured_at").HasColumnType("timestamptz");
        b.HasOne<ReleaseRecord>().WithMany().HasForeignKey(x=>new{x.ReleaseId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ApplicationCredentialConfiguration : IEntityTypeConfiguration<ApplicationCredential>
{
    public void Configure(EntityTypeBuilder<ApplicationCredential> b)
    {
        b.ToTable("application_credentials"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ApplicationId).HasColumnName("application_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.AccessKey).HasColumnName("access_key").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.SecretHash).HasColumnName("secret_hash").HasColumnType("varchar(256)").IsRequired();
        b.Property(x => x.SecretLast4).HasColumnName("secret_last4").HasColumnType("varchar(8)").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.ValidFrom).HasColumnName("valid_from").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class UserRecordConfiguration : IEntityTypeConfiguration<UserRecord>
{
    public void Configure(EntityTypeBuilder<UserRecord> b)
    {
        b.ToTable("users"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.Username).HasColumnName("username").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.DisplayName).HasColumnName("display_name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.Email).HasColumnName("email").HasColumnType("varchar(256)");
        b.Property(x => x.PasswordHash).HasColumnName("password_hash").HasColumnType("varchar(256)");
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.AuthSource).HasColumnName("auth_source").HasColumnType("varchar(24)").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x => x.SecurityStamp).HasColumnName("security_stamp").HasColumnType("varchar(128)").IsRequired().HasDefaultValueSql("gen_random_uuid()::text");
        b.Property(x => x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().IsConcurrencyToken().HasDefaultValue(1L);
    }
}

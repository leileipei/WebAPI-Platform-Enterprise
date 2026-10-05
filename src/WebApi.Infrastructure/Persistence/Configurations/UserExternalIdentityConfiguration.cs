using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class UserExternalIdentityConfiguration:IEntityTypeConfiguration<UserExternalIdentity>
{
    public void Configure(EntityTypeBuilder<UserExternalIdentity> b)
    {
        b.ToTable("user_external_identities",t=>{t.HasCheckConstraint("ck_external_identity_revision","revision > 0");});b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired();
        b.Property(x=>x.UserId).HasColumnName("user_id").HasColumnType("uuid").IsRequired();
        b.Property(x=>x.ProviderId).HasColumnName("provider_id").HasColumnType("uuid").IsRequired();
        b.Property(x=>x.Issuer).HasColumnName("issuer").HasColumnType("varchar(1024)").IsRequired();
        b.Property(x=>x.Subject).HasColumnName("subject").HasColumnType("varchar(255)").IsRequired();
        b.Property(x=>x.Enabled).HasColumnName("enabled").HasColumnType("boolean").IsRequired().HasDefaultValue(true);
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().HasDefaultValue(1L).IsConcurrencyToken();
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        b.Property(x=>x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        b.HasOne<SsoProvider>().WithMany().HasForeignKey(x=>x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>x.UserId).IsUnique();b.HasIndex(x=>new{x.ProviderId,x.Issuer,x.Subject}).IsUnique();
    }
}

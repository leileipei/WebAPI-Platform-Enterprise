using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class SsoLoginAttemptConfiguration:IEntityTypeConfiguration<SsoLoginAttempt>
{
    public void Configure(EntityTypeBuilder<SsoLoginAttempt> b)
    {
        b.ToTable("sso_login_attempts",t=>{t.HasCheckConstraint("ck_sso_attempt_state","state IN ('Pending','Processing','Succeeded','Failed')");t.HasCheckConstraint("ck_sso_attempt_revision","provider_revision > 0 AND auth_revision > 0");t.HasCheckConstraint("ck_sso_attempt_expiry","expires_at > created_at");});b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired();
        b.Property(x=>x.ProviderId).HasColumnName("provider_id").HasColumnType("uuid").IsRequired();
        b.Property(x=>x.ProviderRevision).HasColumnName("provider_revision").HasColumnType("bigint").IsRequired();
        b.Property(x=>x.AuthRevision).HasColumnName("auth_revision").HasColumnType("bigint").IsRequired().HasDefaultValue(1L).IsConcurrencyToken();
        b.Property(x=>x.ReturnPath).HasColumnName("return_path").HasColumnType("varchar(2048)").IsRequired();
        b.Property(x=>x.State).HasColumnName("state").HasColumnType("varchar(24)").IsRequired();
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        b.Property(x=>x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz").IsRequired();
        b.Property(x=>x.FailureCode).HasColumnName("failure_code").HasColumnType("varchar(64)");
        b.HasOne<SsoProvider>().WithMany().HasForeignKey(x=>x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>new{x.State,x.ExpiresAt});
    }
}

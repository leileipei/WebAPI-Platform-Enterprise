using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class SsoProviderConfiguration:IEntityTypeConfiguration<SsoProvider>
{
    public void Configure(EntityTypeBuilder<SsoProvider> b)
    {
        b.ToTable("sso_providers",t=>{t.HasCheckConstraint("ck_sso_provider_default","NOT is_default OR enabled");t.HasCheckConstraint("ck_sso_provider_revision","revision > 0 AND auth_revision > 0");t.HasCheckConstraint("ck_sso_provider_type","provider_type = 'oidc'");t.HasCheckConstraint("ck_sso_provider_json","jsonb_typeof(scopes) = 'array' AND jsonb_typeof(claim_mapping) = 'object'");});b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired();
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.Name).HasColumnName("name").HasColumnType("varchar(128)").IsRequired();
        b.Property(x=>x.ProviderType).HasColumnName("provider_type").HasColumnType("varchar(24)").IsRequired().HasDefaultValue("oidc");
        b.Property(x=>x.Issuer).HasColumnName("issuer").HasColumnType("varchar(1024)").IsRequired();
        b.Property(x=>x.ClientId).HasColumnName("client_id").HasColumnType("varchar(256)").IsRequired();
        b.Property(x=>x.ProtectedSecretFingerprint).HasColumnName("protected_secret_fingerprint").HasColumnType("text");
        b.Property(x=>x.SecretRef).HasColumnName("secret_ref").HasColumnType("varchar(512)").IsRequired();
        b.Property(x=>x.ScopesJson).HasColumnName("scopes").HasColumnType("jsonb").IsRequired();
        b.Property(x=>x.ClaimMappingJson).HasColumnName("claim_mapping").HasColumnType("jsonb").IsRequired();
        b.Property(x=>x.Enabled).HasColumnName("enabled").HasColumnType("boolean").IsRequired().HasDefaultValue(false);
        b.Property(x=>x.IsDefault).HasColumnName("is_default").HasColumnType("boolean").IsRequired().HasDefaultValue(false);
        b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired().HasDefaultValue(1L).IsConcurrencyToken();
        b.Property(x=>x.AuthRevision).HasColumnName("auth_revision").HasColumnType("bigint").IsRequired().HasDefaultValue(1L).IsConcurrencyToken();
        b.Property(x=>x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        b.Property(x=>x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        b.HasOne<Organization>().WithMany().HasForeignKey(x=>x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>x.IsDefault).IsUnique().HasFilter("organization_id IS NULL AND is_default AND enabled").HasDatabaseName("ux_sso_default_platform");
        b.HasIndex(x=>x.OrganizationId).IsUnique().HasFilter("organization_id IS NOT NULL AND is_default AND enabled").HasDatabaseName("ux_sso_default_organization");
    }
}

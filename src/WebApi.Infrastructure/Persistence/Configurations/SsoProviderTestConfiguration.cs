using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class SsoProviderTestConfiguration:IEntityTypeConfiguration<SsoProviderTest>
{
    public void Configure(EntityTypeBuilder<SsoProviderTest> b)
    {
        b.ToTable("sso_provider_tests",t=>{t.HasCheckConstraint("ck_sso_test_revision","provider_revision > 0");});b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired();
        b.Property(x=>x.ProviderId).HasColumnName("provider_id").HasColumnType("uuid").IsRequired();
        b.Property(x=>x.ProviderRevision).HasColumnName("provider_revision").HasColumnType("bigint").IsRequired();
        b.Property(x=>x.TestedAt).HasColumnName("tested_at").HasColumnType("timestamptz").IsRequired().HasDefaultValueSql("now()");
        b.Property(x=>x.Status).HasColumnName("status").HasColumnType("varchar(24)").IsRequired();
        b.Property(x=>x.StagesJson).HasColumnName("stages").HasColumnType("jsonb").IsRequired();
        b.HasOne<SsoProvider>().WithMany().HasForeignKey(x=>x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>new{x.ProviderId,x.TestedAt});
    }
}

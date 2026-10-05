using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class SystemSettingConfiguration:IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> b)
    {
        b.ToTable("system_settings",t=>{t.HasCheckConstraint("ck_system_setting_scope","scope_type = 'system' AND scope_id IS NULL");t.HasCheckConstraint("ck_system_setting_key","key IN ('system.security','system.release','system.gateway','system.audit','system.notification')");t.HasCheckConstraint("ck_system_setting_revision","revision > 0");});b.HasKey(x=>x.Key);
        b.Property(x=>x.Key).HasColumnName("key").HasColumnType("varchar(128)");b.Property(x=>x.ScopeType).HasColumnName("scope_type").HasColumnType("varchar(24)").IsRequired();b.Property(x=>x.ScopeId).HasColumnName("scope_id").HasColumnType("uuid");b.Property(x=>x.Value).HasColumnName("value").HasColumnType("jsonb").IsRequired();b.Property(x=>x.UpdatedBy).HasColumnName("updated_by").HasColumnType("uuid");b.Property(x=>x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");b.Property(x=>x.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken().HasDefaultValue(1L);b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

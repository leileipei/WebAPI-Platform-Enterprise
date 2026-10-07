using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class NotificationChannelProfileConfiguration:IEntityTypeConfiguration<NotificationChannelProfile>
{
    public void Configure(EntityTypeBuilder<NotificationChannelProfile> b)
    {
        AlertColumns.Map(b,"notification_channel_profiles");b.Property(x=>x.PrivateConfiguration).HasColumnType("jsonb");b.HasAlternateKey(x=>new{x.Id,x.Channel});
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>new{x.Channel,x.CreatedAt});
        b.ToTable(t=>{
            t.HasCheckConstraint("ck_notification_profile_channel","channel IN ('Email','Webhook')");
            t.HasCheckConstraint("ck_notification_profile_config","configuration_hash ~ '^[a-f0-9]{64}$' AND jsonb_typeof(private_configuration)='object' AND octet_length(private_configuration::text) BETWEEN 2 AND 16384 AND length(protected_secret_fingerprint) BETWEEN 1 AND 4096 AND settings_revision > 0");
        });
    }
}

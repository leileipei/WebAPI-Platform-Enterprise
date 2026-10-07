using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class NotificationChannelStateConfiguration:IEntityTypeConfiguration<NotificationChannelState>
{
    public void Configure(EntityTypeBuilder<NotificationChannelState> b)
    {
        b.ToTable("notification_channel_states");b.HasKey(x=>x.Channel);b.Property(x=>x.Channel).HasColumnName("channel").HasColumnType("text");b.Property(x=>x.ProfileId).HasColumnName("profile_id");b.Property(x=>x.Enabled).HasColumnName("enabled");b.Property(x=>x.Revision).HasColumnName("revision").IsConcurrencyToken();
        b.HasOne<NotificationChannelProfile>().WithMany().HasForeignKey(x=>new{x.ProfileId,x.Channel}).HasPrincipalKey(x=>new{x.Id,x.Channel}).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_notification_channel_state","channel IN ('Email','Webhook') AND revision > 0 AND (NOT enabled OR profile_id IS NOT NULL)"));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class NotificationDeliveryConfiguration:IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> b)
    {
        AlertColumns.Map(b,"notification_deliveries");
        b.HasAlternateKey(x=>new{x.Id,x.Channel,x.TargetHash});
        b.HasOne<AlertEvent>().WithMany().HasForeignKey(x=>new{x.EventId,x.OrganizationId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId,x.ProjectId,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AlertEventTransition>().WithMany().HasForeignKey(x=>new{x.TransitionId,x.EventId}).HasPrincipalKey(x=>new{x.Id,x.EventId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NotificationDelivery>().WithMany().HasForeignKey(x=>new{x.TriggeredDeliveryId,x.Channel,x.TargetHash}).HasPrincipalKey(x=>new{x.Id,x.Channel,x.TargetHash}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NotificationChannelProfile>().WithMany().HasForeignKey(x=>new{x.ProfileId,x.Channel}).HasPrincipalKey(x=>new{x.Id,x.Channel}).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>new{x.EventId,x.TransitionId,x.Channel,x.TargetHash}).IsUnique();
        b.HasIndex(x=>new{x.Status,x.NextAttemptAt,x.CreatedAt});b.HasIndex(x=>x.LeaseUntil).HasFilter("status = 'Sending'");
        b.HasIndex(x=>new{x.EventId,x.CreatedAt,x.Id});b.HasIndex(x=>new{x.Kind,x.CreatedBy,x.Channel,x.CreatedAt});
        b.ToTable(t=>{
            t.HasCheckConstraint("ck_notification_delivery_scope","(kind='Alert' AND event_id IS NOT NULL AND transition_id IS NOT NULL AND organization_id IS NOT NULL AND project_id IS NOT NULL AND environment_id IS NOT NULL) OR (kind='Test' AND event_id IS NULL AND transition_id IS NULL AND triggered_delivery_id IS NULL AND organization_id IS NULL AND project_id IS NULL AND environment_id IS NULL AND created_by IS NOT NULL)");
            t.HasCheckConstraint("ck_notification_delivery_target","channel IN ('Email','Webhook') AND length(target) BETWEEN 1 AND 2048 AND (channel <> 'Email' OR length(target) <= 254) AND target_hash ~ '^[a-f0-9]{64}$' AND octet_length(payload) BETWEEN 1 AND 16384");
            t.HasCheckConstraint("ck_notification_delivery_status","status IN ('Queued','Sending','RetryScheduled','Paused','Accepted','Failed','Suppressed','Expired') AND (profile_id IS NOT NULL OR (status IN ('Suppressed','Expired') AND reason IS NOT NULL)) AND (reason IS NULL OR reason ~ '^[A-Za-z][A-Za-z0-9]{0,63}$')");
            t.HasCheckConstraint("ck_notification_delivery_budget","max_attempts BETWEEN 1 AND 5 AND base_delay_seconds BETWEEN 1 AND 300 AND max_delay_seconds BETWEEN base_delay_seconds AND 3600 AND expires_after_minutes BETWEEN 5 AND 1440 AND attempt_count BETWEEN 0 AND max_attempts AND expires_at > created_at AND expires_at <= created_at + make_interval(mins => expires_after_minutes) AND revision > 0 AND lease_token >= 0 AND (kind <> 'Test' OR (max_attempts=1 AND expires_after_minutes=5))");
            t.HasCheckConstraint("ck_notification_delivery_lease","(status='Sending' AND lease_owner IS NOT NULL AND length(lease_owner) BETWEEN 1 AND 128 AND lease_until IS NOT NULL AND lease_token > 0 AND attempt_count > 0) OR (status<>'Sending' AND lease_owner IS NULL AND lease_until IS NULL)");
        });
    }
}

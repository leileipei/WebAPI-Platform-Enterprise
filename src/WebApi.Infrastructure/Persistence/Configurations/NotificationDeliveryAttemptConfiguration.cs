using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class NotificationDeliveryAttemptConfiguration:IEntityTypeConfiguration<NotificationDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<NotificationDeliveryAttempt> b)
    {
        AlertColumns.Map(b,"notification_delivery_attempts");b.HasOne<NotificationDelivery>().WithMany().HasForeignKey(x=>x.DeliveryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>new{x.DeliveryId,x.AttemptNo}).IsUnique();b.HasIndex(x=>new{x.DeliveryId,x.LeaseToken}).IsUnique();
        b.ToTable(t=>{
            t.HasCheckConstraint("ck_notification_attempt_budget","attempt_no BETWEEN 1 AND 5 AND lease_token > 0 AND (completed_at IS NULL OR completed_at >= started_at)");
            t.HasCheckConstraint("ck_notification_attempt_outcome","(completed_at IS NULL AND outcome IS NULL AND code IS NULL AND protocol_status IS NULL) OR (completed_at IS NOT NULL AND outcome IS NOT NULL AND outcome IN ('Accepted','TransientFailure','PermanentFailure','OutcomeUnknown') AND code IS NOT NULL AND code ~ '^[A-Za-z][A-Za-z0-9]{0,63}$' AND (protocol_status IS NULL OR protocol_status BETWEEN 100 AND 599))");
        });
    }
}

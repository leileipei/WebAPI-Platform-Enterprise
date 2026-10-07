using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class AlertEventTransitionConfiguration:IEntityTypeConfiguration<AlertEventTransition>
{
    public void Configure(EntityTypeBuilder<AlertEventTransition> b)
    {
        AlertColumns.Map(b,"alert_event_transitions");b.Property(x=>x.Reason).HasMaxLength(512);b.Property(x=>x.CorrelationId).HasMaxLength(128);
        b.HasAlternateKey(x=>new{x.Id,x.EventId});
        b.HasIndex(x=>new{x.EventId,x.OccurredAt});
        b.ToTable(t=>t.HasCheckConstraint("ck_alert_transition_status","(from_status IS NULL OR from_status IN ('Open','Ack','Silenced','Resolved')) AND to_status IN ('Open','Ack','Silenced','Resolved')"));
    }
}

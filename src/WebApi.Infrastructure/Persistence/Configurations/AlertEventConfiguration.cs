using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class AlertEventConfiguration:IEntityTypeConfiguration<AlertEvent>
{
    public void Configure(EntityTypeBuilder<AlertEvent> b)
    {
        AlertColumns.Map(b,"alert_events");
        b.Property(x=>x.ResourceKey).HasMaxLength(128);b.Property(x=>x.Message).HasMaxLength(512);b.Property(x=>x.RuleSummary).HasMaxLength(512);
        b.HasAlternateKey(x=>new{x.Id,x.RuleId,x.EnvironmentId,x.ResourceKey});
        b.HasIndex(x=>new{x.RuleId,x.LogicRevision,x.EnvironmentId,x.ResourceKey,x.OccurrenceNo}).IsUnique();
        b.HasIndex(x=>new{x.RuleId,x.EnvironmentId,x.ResourceKey}).IsUnique().HasFilter("status <> 'Resolved'");
        b.HasIndex(x=>new{x.EnvironmentId,x.Status,x.StartedAt});
        b.ToTable(t=>{
            t.HasCheckConstraint("ck_alert_event_status","status IN ('Open','Ack','Silenced','Resolved') AND evaluation_state IN ('Known','Unknown','ScopeInactive')");
            t.HasCheckConstraint("ck_alert_event_resource","length(resource_key) BETWEEN 1 AND 128 AND ((resource_type='Environment' AND resource_id IS NULL) OR (resource_type IN ('Api','Destination') AND resource_id IS NOT NULL))");
            t.HasCheckConstraint("ck_alert_event_revision","revision > 0 AND rule_revision > 0 AND logic_revision > 0 AND occurrence_no > 0");
            t.HasCheckConstraint("ck_alert_event_severity","severity IN ('Info','Warning','Critical')");
        });
    }
}

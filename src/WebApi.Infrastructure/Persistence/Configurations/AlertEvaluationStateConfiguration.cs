using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class AlertEvaluationStateConfiguration:IEntityTypeConfiguration<AlertEvaluationState>
{
    public void Configure(EntityTypeBuilder<AlertEvaluationState> b)
    {
        AlertColumns.Map(b,"alert_evaluation_states");b.Property(x=>x.ResourceKey).HasMaxLength(128);b.Property(x=>x.LeaseOwner).HasMaxLength(128);
        b.HasIndex(x=>new{x.RuleId,x.LogicRevision,x.EnvironmentId,x.ResourceKey}).IsUnique();b.HasIndex(x=>new{x.LeaseUntil,x.LastEvaluatedSlot});
        b.ToTable(t=>{
            t.HasCheckConstraint("ck_alert_evaluation_phase","phase IN ('Inactive','Pending','Firing','SuppressedUntilRecovery') AND evaluation_state IN ('Known','Unknown','ScopeInactive')");
            t.HasCheckConstraint("ck_alert_evaluation_resource","length(resource_key) BETWEEN 1 AND 128 AND ((resource_type='Environment' AND resource_id IS NULL) OR (resource_type IN ('Api','Destination') AND resource_id IS NOT NULL))");
            t.HasCheckConstraint("ck_alert_evaluation_revision","logic_revision > 0 AND revision > 0 AND lease_token >= 0 AND next_occurrence_no > 0");
        });
    }
}

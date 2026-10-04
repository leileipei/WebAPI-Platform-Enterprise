using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Common;
using WebApi.Contracts.Observability;
using WebApi.Domain.Alerts;
using WebApi.Infrastructure.Observability;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Alerts;
public sealed class AlertEvaluationService(WebApiDbContext db,AlertRuleScopeResolver scopes,PrometheusMetricSource source,AlertEvaluationSettings settings,ILogger<AlertEvaluationService> logger)
{
    public async Task<bool> EvaluateAsync(EvaluationLease lease,CancellationToken ct)
    {
        var rule=await db.Set<AlertRule>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==lease.RuleId,ct);
        if(rule is null||!rule.Enabled||rule.LogicRevision!=lease.LogicRevision)return false;
        var before=await db.Set<AlertEvaluationState>().AsNoTracking().SingleOrDefaultAsync(x=>x.RuleId==lease.RuleId&&x.LogicRevision==lease.LogicRevision&&x.EnvironmentId==lease.EnvironmentId&&x.ResourceKey==lease.ResourceKey,ct);
        if(before is null||before.LeaseToken!=lease.Token||before.LeaseUntil is null||before.LeaseUntil<=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct))return false;
        var active=(await scopes.ResolveForSystemAsync(rule,ct)).Contains(lease.EnvironmentId);var retired=!await TargetExistsAsync(rule,ct);
        var input=new EvaluationInput(lease.Slot,null,null,null,SourceState.Unavailable);
        if(active&&!retired&&rule.Revision==lease.RuleRevision)
        {
            // No transaction/governance lock is held during source I/O.
            var trusted=await scopes.ObservationAsync(rule.OrganizationId,lease.EnvironmentId,ct);var end=lease.Slot.AddSeconds(-settings.QueryDelaySeconds);
            try
            {
                var response=await source.QueryAsync(trusted,new(end.AddSeconds(-rule.WindowSeconds),end),new(rule.TargetType=="Api"?rule.TargetId:null,null,rule.TargetType=="Destination"?rule.TargetId:null),ct);
                var metric=response.Data?.Kpis.SingleOrDefault(x=>x.Metric==rule.Metric);var known=IsKnown(response,metric,rule.Metric);
                input=new(lease.Slot,response.ObservedAt,known?metric!.Value:null,known?AlertExpressionParser.Compare(AlertExpressionParser.Parse(rule.Metric,rule.Expression),metric!.Value):null,known?SourceState.Available:response.SourceState);
            }
            catch(ApiException error)when(error.Status==503){logger.LogWarning("Alert observation unavailable: source_unavailable metrics");}
        }
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
        var current=await db.Set<AlertRule>().FromSqlInterpolated($"SELECT * FROM alert_rules WHERE id={lease.RuleId} FOR UPDATE").SingleOrDefaultAsync(ct);
        var state=await db.Set<AlertEvaluationState>().FromSqlInterpolated($"SELECT * FROM alert_evaluation_states WHERE rule_id={lease.RuleId} AND logic_revision={lease.LogicRevision} AND environment_id={lease.EnvironmentId} AND resource_key={lease.ResourceKey} FOR UPDATE").SingleOrDefaultAsync(ct);
        var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);
        if(current is null||state is null||state.LeaseToken!=lease.Token||state.LeaseUntil is null||state.LeaseUntil<=now||state.LastEvaluatedSlot>=lease.Slot||!current.Enabled||current.LogicRevision!=lease.LogicRevision)
        {await tx.CommitAsync(ct);return false;}
        if(current.Revision!=lease.RuleRevision)
        {
            // A rename invalidates the result but must not strand Pending behind the old lease.
            state.LeaseOwner=null;state.LeaseUntil=null;state.Revision++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return false;
        }
        active=(await scopes.ResolveForSystemAsync(current,ct)).Contains(state.EnvironmentId);retired=!await TargetExistsAsync(current,ct);
        var alert=await db.Set<AlertEvent>().FromSqlInterpolated($"SELECT * FROM alert_events WHERE rule_id={current.Id} AND environment_id={state.EnvironmentId} AND resource_key={state.ResourceKey} AND status <> 'Resolved' FOR UPDATE").SingleOrDefaultAsync(ct);
        var correlation=$"alert:{current.Id:N}:{lease.Token}";
        if(!active||retired)
        {
            if((retired&&active)||state.Phase=="Pending"){state.Phase="Inactive";state.PendingSince=null;}
            if(retired&&active)state.SuppressedAt=null;
            state.LastCondition=null;state.EvaluationState="ScopeInactive";
            if(alert is not null){alert.EvaluationState="ScopeInactive";alert.LastCondition=null;alert.LastValue=null;}
            if(retired&&active&&alert is not null)Resolve(alert,"ResourceRetired",now,correlation);
        }
        else
        {
            var snapshot=new EvaluationStateSnapshot(Enum.Parse<EvaluationPhase>(state.Phase),state.PendingSince,state.LastSuccessAt,state.LastCondition,state.SuppressedAt);
            var decision=AlertEvaluationMachine.Decide(snapshot,input,current.ForSeconds,settings.IntervalSeconds);
            var known=input.SourceState==SourceState.Available&&input.ObservedAt is not null&&input.ObservedAt<=input.Slot&&input.Value is double value&&double.IsFinite(value)&&input.Condition is not null;
            var advances=known&&(state.LastSuccessAt is null||input.ObservedAt>state.LastSuccessAt);
            state.Phase=decision.NewPhase.ToString();state.PendingSince=decision.PendingSince;state.LastCondition=decision.LastCondition;state.EvaluationState=known?"Known":"Unknown";
            if(advances)state.LastSuccessAt=input.ObservedAt;
            if(decision.NewPhase!=EvaluationPhase.SuppressedUntilRecovery)state.SuppressedAt=null;
            if(decision.CreateEvent&&alert is null)
            {
                alert=new(){RuleId=current.Id,RuleRevision=current.Revision,LogicRevision=current.LogicRevision,OrganizationId=state.OrganizationId,ProjectId=state.ProjectId,EnvironmentId=state.EnvironmentId,ResourceKey=state.ResourceKey,ResourceType=state.ResourceType,ResourceId=state.ResourceId,OccurrenceNo=state.NextOccurrenceNo++,Severity=current.Severity,Message=$"{current.Metric} 满足告警阈值。",RuleSummary=current.Name+" · "+current.Expression,StartedAt=now,ConditionStartedAt=decision.PendingSince??input.ObservedAt??now,EvaluationState="Known"};db.Add(alert);state.LastEventId=alert.Id;
                db.Add(new AlertEventTransition{EventId=alert.Id,ToStatus="Open",Reason="Triggered",OccurredAt=now,CorrelationId=correlation});AlertSystemAudit.Add(db,alert,"alert.triggered","Triggered",now,correlation);
            }
            if(alert is not null)
            {
                alert.EvaluationState=known?"Known":"Unknown";alert.LastCondition=decision.LastCondition;
                if(advances){alert.LastObservedAt=input.ObservedAt;alert.LastValue=input.Value;}else if(!known)alert.LastValue=null;
                if(decision.ResolveReason is not null)Resolve(alert,decision.ResolveReason,now,correlation);
                // The event command revision protects lifecycle facts. Observation metadata
                // is already serialized by the rule/state/event locks and lease token.
            }
        }
        state.LastEvaluatedSlot=lease.Slot;state.LeaseOwner=null;state.LeaseUntil=null;state.Revision++;
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;
    }
    internal static bool IsKnown(ObservationEnvelope<MetricsDto> response,MetricValueDto? metric,string metricName)=>response.Coverage.Complete&&metric?.Value is double value&&double.IsFinite(value)&&(response.SourceState==SourceState.Available||(response.SourceState==SourceState.NoData&&metricName=="unhealthy_destinations"));
    internal async Task<bool> TargetExistsAsync(AlertRule rule,CancellationToken ct)
    {
        if(rule.TargetType=="Environment")return true;
        if(rule.TargetType=="Api")return await db.Set<Api>().AnyAsync(x=>x.Id==rule.TargetId&&x.ProjectId==rule.ProjectId&&x.OrganizationId==rule.OrganizationId,ct);
        return await(from d in db.Set<UpstreamDestination>() join c in db.Set<UpstreamCluster>() on d.ClusterId equals c.Id where d.Id==rule.TargetId&&c.ProjectId==rule.ProjectId&&c.EnvironmentId==rule.EnvironmentId select d.Id).AnyAsync(ct);
    }
    private void Resolve(AlertEvent alert,string reason,DateTimeOffset now,string correlation)
    {
        var from=alert.Status;alert.Status="Resolved";alert.ResolvedAt=now;alert.ResolvedBy=null;alert.ResolveReason=reason;alert.SilencedUntil=null;alert.Revision++;
        db.Add(new AlertEventTransition{EventId=alert.Id,FromStatus=from,ToStatus="Resolved",Reason=reason,OccurredAt=now,CorrelationId=correlation});AlertSystemAudit.Add(db,alert,"alert.resolved",reason,now,correlation);
    }
}

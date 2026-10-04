using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Common;
using WebApi.Contracts.Observability;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Observability;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Alerts;
public sealed class AlertEventService(WebApiDbContext db,AuthorizationService authorization,ObservationScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext)
{
    internal static ScopeRef Scope(AlertEvent e)=>new(e.OrganizationId,e.ProjectId,e.EnvironmentId);
    internal static async Task<AlertEventDto> DtoAsync(WebApiDbContext db,AlertEvent e,CancellationToken ct)
    {
        var persisted=await db.Set<AlertEventTransition>().AsNoTracking().Where(x=>x.EventId==e.Id).ToArrayAsync(ct);
        var added=db.ChangeTracker.Entries<AlertEventTransition>().Where(x=>x.State==EntityState.Added&&x.Entity.EventId==e.Id).Select(x=>x.Entity);
        var transitions=persisted.Concat(added).OrderBy(x=>x.OccurredAt).ThenBy(x=>x.Id).Select(x=>new AlertTransitionDto(x.Id,x.FromStatus,x.ToStatus,x.ActorId,x.Reason,x.OccurredAt,x.CorrelationId)).ToArray();
        return new(e.Id,e.RuleId,e.RuleRevision,e.LogicRevision,Scope(e),e.ResourceKey,e.ResourceType,e.ResourceId,e.OccurrenceNo,e.Status,e.Severity,e.Message,e.RuleSummary,e.StartedAt,e.ConditionStartedAt,e.ResolvedAt,e.AckedBy,e.AckedAt,e.SilencedBy,e.SilencedUntil,e.SilenceReason,e.ResolvedBy,e.ResolveReason,e.LastObservedAt,e.LastValue,e.LastCondition,e.EvaluationState,e.Revision,transitions);
    }
    public async Task<AlertEventDto> DetailAsync(ActorContext actor,Guid id,CancellationToken ct)
    {
        var e=await db.Set<AlertEvent>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();
        if(!await authorization.CanAsync(actor,"alert.read",new("alert",e.Id,Scope(e)),ct))throw ScopeResolver.Missing();return await DtoAsync(db,e,ct);
    }
    public async Task<PageResult<AlertEventDto>> ListAsync(ActorContext actor,ObservationScopeRequest scope,AlertListFilter filter,CancellationToken ct)
    {
        if(filter.Severity is not(null or "Info" or "Warning" or "Critical")||filter.Source is not(null or "Metrics")||filter.Status is not(null or "Open" or "Ack" or "Resolved" or "Silenced"))throw new ApiException(422,"invalid_alert_filter","告警级别、来源或状态筛选不合法。");
        var trusted=await scopes.ResolveAsync(actor,"alert.read",scope,null,null,null,ct);
        var rows=await db.Set<AlertEvent>().AsNoTracking().Where(x=>x.OrganizationId==scope.OrganizationId&&x.ProjectId==scope.ProjectId&&trusted.EnvironmentIds.Contains(x.EnvironmentId)&&(filter.Severity==null||x.Severity==filter.Severity)&&(filter.Status==null||x.Status==filter.Status)).OrderByDescending(x=>x.StartedAt).ThenByDescending(x=>x.Id).ToArrayAsync(ct);
        var slice=Pagination.Slice(rows,filter.Page,filter.PageSize);var data=new List<AlertEventDto>();foreach(var e in slice.Items)data.Add(await DtoAsync(db,e,ct));return new(data,slice.Total,slice.Page,slice.PageSize);
    }
    public async Task<AlertEventDto> ActAsync(ActorContext actor,Guid id,AlertAction input,string etag,CancellationToken ct)
    {
        var action=Validate(input);var original=await db.Set<AlertEvent>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();var scope=Scope(original);var operation="alert."+action.Kind.ToLowerInvariant();
        return await commands.ExecuteAsync(actor,scope,operation,async(_,token)=>{
            // First permission read is inside governance lock; no stale tracked grant from preauthorization.
            await authorization.RequireAsync(actor,"alert.operate",new("alert",id,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,operation,requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,action,etag}),async inner=>{
                var metadata=await db.Set<AlertEvent>().AsNoTracking().SingleAsync(x=>x.Id==id,inner);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_rules WHERE id={metadata.RuleId} FOR UPDATE",inner);
                var state=await db.Set<AlertEvaluationState>().FromSqlInterpolated($"SELECT * FROM alert_evaluation_states WHERE rule_id={metadata.RuleId} AND logic_revision={metadata.LogicRevision} AND environment_id={metadata.EnvironmentId} AND resource_key={metadata.ResourceKey} FOR UPDATE").SingleOrDefaultAsync(inner);
                var e=await db.Set<AlertEvent>().FromSqlInterpolated($"SELECT * FROM alert_events WHERE id={id} FOR UPDATE").SingleAsync(inner);await authorization.RequireAsync(actor,"alert.operate",new("alert",id,Scope(e)),inner);RevisionTag.Require(etag,e.Revision);
                if(e.Status=="Resolved")throw new ApiException(409,"alert_resolved","该事件已解决，不能重新打开原事件。");
                var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,inner);var from=e.Status;var changed=true;string reason;
                switch(action.Kind)
                {
                    case "Ack":
                        reason="Ack";if(e.AckedBy is not null){changed=false;break;}e.AckedBy=actor.UserId;e.AckedAt=now;if(e.Status!="Silenced")e.Status="Ack";break;
                    case "Silence":
                        reason=action.Reason!;var until=action.DurationSeconds is int duration?now.AddSeconds(duration):action.Until!.Value.ToUniversalTime();
                        if(until-now<TimeSpan.FromMinutes(15)||until-now>TimeSpan.FromHours(24))throw new ApiException(422,"invalid_silence_window","静默截止必须在实际提交时点的15分钟至24小时内。");
                        e.Status="Silenced";e.SilencedBy=actor.UserId;e.SilencedUntil=until;e.SilenceReason=reason;break;
                    case "Unsilence":
                        if(e.Status!="Silenced")throw new ApiException(409,"alert_not_silenced","当前事件不处于静默状态。");reason="Unsilenced";e.Status=e.AckedBy is null?"Open":"Ack";e.SilencedUntil=null;break;
                    default:
                        reason=action.Reason!;e.Status="Resolved";e.ResolvedBy=actor.UserId;e.ResolvedAt=now;e.ResolveReason=reason;e.SilencedUntil=null;
                        if(state is null){state=new(){RuleId=e.RuleId,LogicRevision=e.LogicRevision,OrganizationId=e.OrganizationId,ProjectId=e.ProjectId,EnvironmentId=e.EnvironmentId,ResourceKey=e.ResourceKey,ResourceType=e.ResourceType,ResourceId=e.ResourceId,LastEventId=e.Id,NextOccurrenceNo=e.OccurrenceNo+1};db.Add(state);}
                        state.Phase="SuppressedUntilRecovery";state.PendingSince=null;state.SuppressedAt=now;state.LastEventId=e.Id;state.LeaseToken++;state.LeaseOwner=null;state.LeaseUntil=null;state.Revision++;break;
                }
                if(changed){e.Revision++;db.Add(new AlertEventTransition{EventId=e.Id,FromStatus=from,ToStatus=e.Status,ActorId=actor.UserId,Reason=reason,OccurredAt=now,CorrelationId=actor.TraceId});}
                return await DtoAsync(db,e,inner);
            },token);
        },ct);
    }
    private static AlertAction Validate(AlertAction a)
    {
        if(a.Kind is not("Ack" or "Resolve" or "Silence" or "Unsilence"))throw new ApiException(422,"invalid_alert_action","不支持的事件命令。");
        var reason=a.Reason?.Trim();if(reason is not null&&(reason.Length>512||reason.Any(c=>char.IsControl(c)&&c is not('\n' or '\r' or '\t'))))throw new ApiException(422,"invalid_alert_reason","原因长度或内容不合法。");
        if(a.Kind is "Resolve" or "Silence"&&string.IsNullOrWhiteSpace(reason))throw new ApiException(422,"alert_reason_required","请填写处理原因。");
        if(a.Kind=="Silence")
        {
            if((a.Until is null)==(a.DurationSeconds is null)||a.DurationSeconds is <900 or >86400)throw new ApiException(422,"invalid_silence_window","请仅提供15分钟至24小时的相对时长或绝对截止时间。");
        }
        else if(a.Until is not null||a.DurationSeconds is not null)throw new ApiException(422,"unexpected_silence_window","此命令不接受静默时间。");
        return a with{Reason=reason,Until=a.Until?.ToUniversalTime()};
    }
}

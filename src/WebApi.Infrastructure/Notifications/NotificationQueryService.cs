using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Settings;
using WebApi.Infrastructure.Policies;
using Microsoft.EntityFrameworkCore;
namespace WebApi.Infrastructure.Notifications;
public sealed class NotificationQueryService(WebApiDbContext db,AuthorizationService authorization,ScopeResolver scopes,NotificationDeliveryStore store,SystemSettingsReader reader,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext)
{
    internal static readonly ScopeRef Platform=new(Guid.Empty);
    internal static ScopeRef Scope(NotificationDelivery row)=>row.Kind=="Test"?Platform:new(row.OrganizationId!.Value,row.ProjectId,row.EnvironmentId);
    internal static NotificationDeliveryDto Dto(NotificationDelivery row,bool retry=false)=>new(row.Id,row.Kind,row.EventId,Enum.Parse<NotificationChannel>(row.Channel),row.Channel=="Email"?"***@***":"https://***/***",Enum.Parse<DeliveryStatus>(row.Status),row.Reason,row.AttemptCount,row.MaxAttempts,row.CreatedAt,row.ExpiresAt,row.NextAttemptAt,retry,row.Revision);
    private static void Page(int page,int size){if(page is <1 or >1000000||size is <1 or >100)throw new ApiException(422,"invalid_notification_page","通知分页参数不合法。");}
    private async Task<NotificationDelivery> VisibleAsync(Guid id,ActorContext actor,CancellationToken ct)
    {var row=await db.Set<NotificationDelivery>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();var permission=row.Kind=="Test"?"system.manage":"alert.read";if(!await authorization.CanAsync(actor,permission,new("notification_delivery",id,Scope(row)),ct))throw ScopeResolver.Missing();return row;}
    public async Task<PageResult<NotificationDeliveryDto>> ListForEventAsync(Guid id,ActorContext actor,int page,int size,CancellationToken ct=default)
    {
        Page(page,size);var alert=await db.Set<AlertEvent>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();if(!await authorization.CanAsync(actor,"alert.read",new("alert",id,AlertEventService.Scope(alert)),ct))throw ScopeResolver.Missing();
        var query=db.Set<NotificationDelivery>().AsNoTracking().Where(x=>x.EventId==id);var total=await query.CountAsync(ct);var rows=await query.OrderByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Id).Skip((page-1)*size).Take(size).ToArrayAsync(ct);var result=new List<NotificationDeliveryDto>();var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);
        foreach(var row in rows)result.Add(Dto(row,await CanRetryAsync(row,actor,now,ct)));return new(result,total,page,size);
    }
    public async Task<PageResult<NotificationAttemptDto>> AttemptsAsync(Guid id,ActorContext actor,int page,int size,CancellationToken ct=default)
    {Page(page,size);await VisibleAsync(id,actor,ct);var query=db.Set<NotificationDeliveryAttempt>().AsNoTracking().Where(x=>x.DeliveryId==id);var count=await query.CountAsync(ct);var rows=await query.OrderByDescending(x=>x.AttemptNo).Skip((page-1)*size).Take(size).Select(x=>new NotificationAttemptDto(x.AttemptNo,x.StartedAt,x.CompletedAt,x.Outcome==null?null:Enum.Parse<DeliveryOutcome>(x.Outcome),x.Code,x.ProtocolStatus)).ToArrayAsync(ct);return new(rows,count,page,size);}
    internal async Task<bool> CanRetryAsync(NotificationDelivery row,ActorContext actor,DateTimeOffset now,CancellationToken ct)
    {
        if(row.Status is not("RetryScheduled" or "Paused" or "Failed")||row.AttemptCount>=row.MaxAttempts||row.ExpiresAt<=now||row.Reason is "AttemptsExhausted" or "RetryAfterExceedsBudget")return false;
        if(!await authorization.CanAsync(actor,row.Kind=="Test"?"system.manage":"alert.operate",new("notification_delivery",row.Id,Scope(row)),ct))return false;
        if(row.Status=="Failed"&&!await db.Set<NotificationDeliveryAttempt>().AsNoTracking().AnyAsync(x=>x.DeliveryId==row.Id&&x.AttemptNo==row.AttemptCount&&(x.Outcome=="TransientFailure"||x.Outcome=="OutcomeUnknown"),ct))return false;
        return (await store.ReadinessAsync(row,ct)).Status is null;
    }
    public async Task<NotificationDeliveryDto> RetryAsync(Guid id,string tag,ActorContext actor,CancellationToken ct=default)
    {
        var original=await VisibleAsync(id,actor,ct);var scope=Scope(original);var permission=original.Kind=="Test"?"system.manage":"alert.operate";
        return await commands.ExecuteAsync(actor,scope,"notification.retry",async(_,token)=>{
            await VisibleAsync(id,actor,token);await authorization.RequireAsync(actor,permission,new("notification_delivery",id,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"notification.retry",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,tag}),async inner=>{
                var row=await store.LockAsync(id,inner)??throw ScopeResolver.Missing();PolicyPreconditions.Require(tag,row.Revision);var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,inner);if(!await CanRetryAsync(row,actor,now,inner))throw new ApiException(409,"notification_retry_unavailable","当前通知不能重试，请查看回执及预算。");
                if(row.Status=="Failed"&&row.NextAttemptAt is null)
                {var previous=await db.Set<NotificationDeliveryAttempt>().AsNoTracking().SingleAsync(x=>x.DeliveryId==id&&x.AttemptNo==row.AttemptCount,inner);var seconds=Math.Min(row.MaxDelaySeconds,row.BaseDelaySeconds*Math.Pow(2,Math.Max(0,row.AttemptCount-1))*1.2);row.NextAttemptAt=previous.CompletedAt!.Value.AddSeconds(seconds);}
                row.Status=row.NextAttemptAt>now?"RetryScheduled":"Queued";row.CompletedAt=null;row.Revision++;return Dto(row,true);
            },token);
        },ct);
    }
    public async Task<NotificationLimitsDto> GetLimitsAsync(ScopeRef scope,ActorContext actor,CancellationToken ct=default)
    {
        if(scope.OrganizationId==Guid.Empty||scope.EnvironmentId is not null&&scope.ProjectId is null)throw new ApiException(422,"notification_scope_required","请选择有效组织及关联范围。");
        if(scope.EnvironmentId is {} environment){var actual=await scopes.EnvironmentAsync(environment,ct);if(actual!=scope)throw ScopeResolver.Missing();}
        else if(scope.ProjectId is {} project){var actual=await scopes.ProjectAsync(project,ct);if(actual!=scope)throw ScopeResolver.Missing();}
        else if(!await db.Set<Organization>().AsNoTracking().AnyAsync(x=>x.Id==scope.OrganizationId,ct))throw ScopeResolver.Missing();
        try {await authorization.RequireAsync(actor,"alert.rule.manage",new("notification_limits",scope.EnvironmentId??scope.ProjectId??scope.OrganizationId,scope),ct);}catch(ApiException error)when(error.Code=="scope_denied"){throw ScopeResolver.Missing();}
        var value=(NotificationSettings)(await reader.ReadAsync("notification",ct)).Value;return new(20,value.SmtpHost is not null&&value.SmtpPort is not null&&value.FromEmail is not null&&value.SmtpSecretRef is not null,value.SmtpEnabled,value.WebhookUrl is not null&&value.WebhookSecretRef is not null,value.WebhookEnabled);
    }
}

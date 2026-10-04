using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Common;
using WebApi.Contracts.Observability;
using WebApi.Contracts.Security;
using WebApi.Domain.Alerts;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Observability;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Alerts;
public sealed class AlertRuleService(WebApiDbContext db,AlertRuleScopeResolver scopes,ObservationScopeResolver observations,PrometheusMetricSource source,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,AlertEvaluationSettings settings)
{
    public static SaveAlertRuleRequest Definition(AlertRule r)=>new(r.OrganizationId,r.ProjectId,r.EnvironmentId,r.Name,r.Metric,r.Expression,r.Severity,r.Enabled,r.ForSeconds,r.TargetType,r.TargetId,r.WindowSeconds,JsonSerializer.Deserialize<NotificationIntent>(r.Notification,CanonicalJson.Options)!);
    public static string ResourceKey(SaveAlertRuleRequest r)=>r.TargetType=="Environment"?"Environment":r.TargetType+":"+r.TargetId!.Value.ToString("D");
    private static ApiException Invalid(string message)=>new(422,"invalid_alert_rule",message);
    private static SaveAlertRuleRequest Validate(SaveAlertRuleRequest r)
    {
        var name=r.Name.Trim().Normalize(NormalizationForm.FormKC);
        if(name.Length is <1 or >128||name.Any(char.IsControl)||r.Severity is not("Info" or "Warning" or "Critical")||r.ForSeconds is <0 or >86400||r.WindowSeconds is <60 or >3600)throw Invalid("规则名称、级别、持续时间或窗口不合法。");
        if(r.TargetType=="Api"&&r.Metric=="unhealthy_destinations")throw Invalid("API目标尚不支持准确的后端健康聚合，请选择环境或具体后端。");
        try{AlertExpressionParser.Parse(r.Metric,r.Expression);}catch(ArgumentException e){throw Invalid(e.Message);}
        if(r.Notification is null||!r.Notification.InConsole||r.Notification.RequestedChannels is null||r.Notification.RequestedChannels.Count>3||r.Notification.RequestedChannels.Any(x=>x is not("Email" or "Webhook" or "EnterpriseIm")))throw Invalid("通知仅支持站内中心及三个未启用渠道的意向配置。");
        return r with{Name=name,Notification=new(true,r.Notification.RequestedChannels.Distinct().Order(StringComparer.Ordinal).ToArray())};
    }
    private async Task<AlertRuleDto> DtoAsync(AlertRule rule,CancellationToken ct)
    {
        var expected=await scopes.ResolveForSystemAsync(rule,ct);var key=ResourceKey(Definition(rule));
        var states=await db.Set<AlertEvaluationState>().AsNoTracking().Where(x=>x.RuleId==rule.Id&&x.LogicRevision==rule.LogicRevision&&x.ResourceKey==key&&expected.Contains(x.EnvironmentId)).ToArrayAsync(ct);
        var allSuccessful=expected.Count>0&&states.Length==expected.Count&&states.All(x=>x.LastSuccessAt is not null);
        var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);
        var complete=allSuccessful&&states.All(x=>x.EvaluationState=="Known"&&x.LastEvaluatedSlot is not null&&x.LastEvaluatedSlot<=now&&now-x.LastEvaluatedSlot<=TimeSpan.FromSeconds(settings.IntervalSeconds*2));
        return new(rule.Id,Definition(rule),rule.Revision,rule.LogicRevision,!rule.Enabled?"Disabled":expected.Count==0?"ScopeInactive":complete?"Known":"Unknown",allSuccessful?states.Min(x=>x.LastSuccessAt):null);
    }
    public async Task<PageResult<AlertRuleDto>> ListAsync(ActorContext actor,ObservationScopeRequest scope,int page,int pageSize,CancellationToken ct)
    {
        var trusted=await observations.ResolveAsync(actor,"alert.rule.manage",scope,null,null,null,ct);
        var candidates=await db.Set<AlertRule>().AsNoTracking().Where(x=>x.OrganizationId==scope.OrganizationId&&(x.ProjectId==null||x.ProjectId==scope.ProjectId)&&(x.EnvironmentId==null||trusted.EnvironmentIds.Contains(x.EnvironmentId.Value))).OrderBy(x=>x.NormalizedName).ThenBy(x=>x.Id).ToArrayAsync(ct);
        var visible=new List<AlertRule>();foreach(var r in candidates){try{await scopes.AuthorizeExistingAsync(actor,r,ct);visible.Add(r);}catch(ApiException e)when(e.Status is 403 or 404 or 409){}}
        var slice=Pagination.Slice(visible,page,pageSize);var results=new List<AlertRuleDto>();foreach(var r in slice.Items)results.Add(await DtoAsync(r,ct));return new(results,slice.Total,slice.Page,slice.PageSize);
    }
    public async Task<AlertRuleDto> DetailAsync(ActorContext actor,Guid id,CancellationToken ct)
    {
        var rule=await db.Set<AlertRule>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();
        try{await scopes.AuthorizeExistingAsync(actor,rule,ct);}catch(ApiException e)when(e.Status is 403 or 404){throw ScopeResolver.Missing();}
        return await DtoAsync(rule,ct);
    }
    public Task<AlertRuleDto> CreateAsync(ActorContext actor,SaveAlertRuleRequest request,CancellationToken ct)=>SaveAsync(actor,null,request,null,ct);
    public Task<AlertRuleDto> UpdateAsync(ActorContext actor,Guid id,SaveAlertRuleRequest request,string etag,CancellationToken ct)=>SaveAsync(actor,id,request,etag,ct);
    public async Task<AlertRuleDto> SetEnabledAsync(ActorContext actor,Guid id,bool enabled,string etag,CancellationToken ct)
    {
        var rule=await db.Set<AlertRule>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();
        return await SaveAsync(actor,id,Definition(rule) with{Enabled=enabled},etag,ct);
    }
    private async Task<AlertRuleDto> SaveAsync(ActorContext actor,Guid? id,SaveAlertRuleRequest input,string? etag,CancellationToken ct)
    {
        var request=Validate(input);var old=id is Guid existing?await db.Set<AlertRule>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==existing,ct)??throw ScopeResolver.Missing():null;
        var initialScope=old is null?AlertRuleScopeResolver.Scope(request):AlertRuleScopeResolver.Scope(Definition(old));var operation=id is null?"alert.rule.create":"alert.rule.update";
        return await commands.ExecuteAsync(actor,initialScope,operation,async(_,token)=>{
            // Authorization is re-read inside the governance transaction, before replaying an idempotent result.
            if(id is Guid known){var current=await db.Set<AlertRule>().AsNoTracking().SingleAsync(x=>x.Id==known,token);await scopes.AuthorizeExistingAsync(actor,current,token);if(current.OrganizationId!=request.OrganizationId)throw Invalid("规则不能迁移到其他组织，请在目标组织新建规则。");}
            await scopes.ResolveForActorAsync(actor,request,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,new ScopeRef(request.OrganizationId),operation,requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,request,etag}),async inner=>{
                AlertRule rule;
                if(id is Guid found){rule=await db.Set<AlertRule>().FromSqlInterpolated($"SELECT * FROM alert_rules WHERE id={found} FOR UPDATE").SingleAsync(inner);RevisionTag.Require(etag,rule.Revision);var previous=Definition(rule);if(LogicChanged(previous,request)){await CloseOldAsync(rule,request.Enabled?"RuleChanged":"RuleDisabled",actor,inner);rule.LogicRevision++;}rule.Revision++;}
                else{rule=new(){CreatedBy=actor.UserId};db.Add(rule);}
                Assign(rule,request);rule.UpdatedBy=actor.UserId;rule.UpdatedAt=DateTimeOffset.UtcNow;return await DtoAsync(rule,inner);
            },token);
        },ct);
    }
    private static bool LogicChanged(SaveAlertRuleRequest a,SaveAlertRuleRequest b)=>a.OrganizationId!=b.OrganizationId||a.ProjectId!=b.ProjectId||a.EnvironmentId!=b.EnvironmentId||a.Metric!=b.Metric||a.Expression!=b.Expression||a.Severity!=b.Severity||a.Enabled!=b.Enabled||a.ForSeconds!=b.ForSeconds||a.WindowSeconds!=b.WindowSeconds||a.TargetType!=b.TargetType||a.TargetId!=b.TargetId;
    private static void Assign(AlertRule r,SaveAlertRuleRequest d){r.OrganizationId=d.OrganizationId;r.ProjectId=d.ProjectId;r.EnvironmentId=d.EnvironmentId;r.Name=d.Name;r.NormalizedName=d.Name.ToUpperInvariant();r.Metric=d.Metric;r.Expression=d.Expression;r.Severity=d.Severity;r.Enabled=d.Enabled;r.ForSeconds=d.ForSeconds;r.TargetType=d.TargetType;r.TargetId=d.TargetId;r.WindowSeconds=d.WindowSeconds;r.Notification=JsonSerializer.Serialize(d.Notification,CanonicalJson.Options);}
    internal async Task CloseOldAsync(AlertRule rule,string reason,ActorContext actor,CancellationToken ct)
    {
        // Global governance -> rule -> evaluation -> event. Source I/O never runs in this transaction.
        var states=await db.Set<AlertEvaluationState>().FromSqlInterpolated($"SELECT * FROM alert_evaluation_states WHERE rule_id={rule.Id} ORDER BY id FOR UPDATE").ToArrayAsync(ct);
        var events=await db.Set<AlertEvent>().FromSqlInterpolated($"SELECT * FROM alert_events WHERE rule_id={rule.Id} AND status <> 'Resolved' ORDER BY id FOR UPDATE").ToArrayAsync(ct);var now=DateTimeOffset.UtcNow;
        foreach(var e in events){var from=e.Status;e.Status="Resolved";e.ResolvedAt=now;e.ResolveReason=reason;e.ResolvedBy=actor.UserId;e.SilencedUntil=null;e.Revision++;db.Add(new AlertEventTransition{EventId=e.Id,FromStatus=from,ToStatus="Resolved",ActorId=actor.UserId,Reason=reason,OccurredAt=now,CorrelationId=actor.TraceId});}
        foreach(var s in states){s.Phase="Inactive";s.PendingSince=null;s.SuppressedAt=null;s.LeaseOwner=null;s.LeaseUntil=null;s.LeaseToken++;s.Revision++;}
    }
    public async Task<RuleScopePreviewDto> PreviewAsync(ActorContext actor,SaveAlertRuleRequest input,CancellationToken ct)
    {
        var r=Validate(input);var envs=await scopes.ResolveForActorAsync(actor,r,ct);return new(envs,r.EnvironmentId is null,r.EnvironmentId is null?"当前范围及未来新增Active环境均会独立评估。":"仅评估所选环境。");
    }
    private static string Unit(string metric)=>metric switch{"request_rps"=>"requests/s","error_5xx_ratio"=>"ratio","latency_p95_ms"=>"ms",_=>"destinations"};
    public async Task<RuleTestDto> TestAsync(ActorContext actor,SaveAlertRuleRequest input,CancellationToken ct)
    {
        var r=Validate(input);var environments=await scopes.ResolveForActorAsync(actor,r,ct);var expression=AlertExpressionParser.Parse(r.Metric,r.Expression);var matches=new List<MetricGroupDto>();var conditions=new List<bool?>();var end=DateTimeOffset.UtcNow.AddSeconds(-30);var range=new TimeRange(end.AddSeconds(-r.WindowSeconds),end);
        foreach(var environment in environments)
        {
            var trusted=await scopes.ObservationAsync(r.OrganizationId,environment,ct);
            ObservationEnvelope<MetricsDto> value;
            try{value=await source.QueryAsync(trusted,range,new(r.TargetType=="Api"?r.TargetId:null,null,r.TargetType=="Destination"?r.TargetId:null),ct);}
            catch(ApiException error)when(error.Status==503)
            {
                matches.Add(new(environment.ToString(),environment.ToString(),[new(r.Metric,null,Unit(r.Metric),0,SourceState.Unavailable)]));conditions.Add(null);continue;
            }
            var metric=value.Data?.Kpis.SingleOrDefault(x=>x.Metric==r.Metric);
            var known=AlertEvaluationService.IsKnown(value,metric,r.Metric);
            matches.Add(new(environment.ToString(),environment.ToString(),[metric is null?new(r.Metric,null,r.Metric=="latency_p95_ms"?"ms":"",0,SourceState.NoData):known?metric with{State=SourceState.Available}:metric with{Value=null,State=value.SourceState==SourceState.Available?SourceState.Partial:value.SourceState}]));conditions.Add(known?AlertExpressionParser.Compare(expression,metric!.Value):null);
        }
        var current=await scopes.ResolveForActorAsync(actor,r,ct);if(!environments.SequenceEqual(current))throw new ApiException(403,"scope_changed","规则范围已变化，请重新预览。");
        return new(conditions.Count==0||conditions.Any(x=>x is null)?"Unknown":"Known",matches,conditions.Count==0||conditions.Any(x=>x is null)?null:conditions.Any(x=>x==true));
    }
}

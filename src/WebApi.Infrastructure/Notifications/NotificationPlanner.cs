using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Domain.Notifications;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace WebApi.Infrastructure.Notifications;
internal sealed record NotificationFrozenSnapshot(NotificationIntent Policy,Guid? EmailProfileId,Guid? WebhookProfileId,string? ClosureKind=null);
public sealed class NotificationPlanner(WebApiDbContext db,AlertRuleScopeResolver scopes,NotificationDeploymentSettings settings)
{
    private static readonly HashSet<string> unstarted=["Queued","RetryScheduled","Paused"];
    public Task OnTransitionAsync(AlertEvent alert,AlertEventTransition transition,CancellationToken ct=default)=>OnTransitionCoreAsync(alert,transition,false,ct);
    internal Task OnGovernanceClosureAsync(AlertEvent alert,AlertEventTransition transition,CancellationToken ct)=>OnTransitionCoreAsync(alert,transition,true,ct);
    private async Task OnTransitionCoreAsync(AlertEvent alert,AlertEventTransition transition,bool governance,CancellationToken ct)
    {
        if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Notification planning requires a lifecycle transaction.");
        if(transition.EventId!=alert.Id)throw new InvalidOperationException("Notification transition does not belong to the event.");
        var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);
        if(transition.Reason=="Triggered"&&transition.FromStatus is null&&transition.ToStatus=="Open"&&transition.ActorId is null)
        {
            var rule=await db.Set<AlertRule>().AsNoTracking().SingleAsync(x=>x.Id==alert.RuleId,ct);
            if(db.Entry(alert).State==EntityState.Added)
            {
                var policy=NotificationPolicyValidator.Parse(rule.Notification);var channels=await db.Set<NotificationChannelState>().AsNoTracking().ToArrayAsync(ct);
                alert.FrozenNotification=Encoding.UTF8.GetString(CanonicalJson.Serialize(new NotificationFrozenSnapshot(policy,channels.SingleOrDefault(x=>x.Channel=="Email")?.ProfileId,channels.SingleOrDefault(x=>x.Channel=="Webhook")?.ProfileId)));
            }
            var frozen=Snapshot(alert);if(!frozen.Policy.ExternalEnabled)return;
            var rows=await RowsAsync(alert.Id,ct);
            foreach(var channel in new[]{"Email","Webhook"}.Where(frozen.Policy.RequestedChannels.Contains))
            {
                var id=channel=="Email"?frozen.EmailProfileId:frozen.WebhookProfileId;
                var profile=id is {} profileId?await db.Set<NotificationChannelProfile>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==profileId,ct):null;
                var targets=channel=="Email"?frozen.Policy.EmailRecipients!:new[]{profile is null?"Webhook":NotificationConfigurationService.Decode(profile).Url!};
                foreach(var target in targets)
                {
                    var hash=TargetHash(target);if(rows.Any(x=>x.TransitionId==transition.Id&&x.Channel==channel&&x.TargetHash==hash))continue;
                    var reason=await UnavailableReasonAsync(alert,channel,id,target,ct);if(settings.ConsoleBaseUrl is null)reason??="ConsoleBaseUrlUnavailable";
                    var payload=NotificationPayload.Alert(alert.Id,alert.OccurrenceNo,alert.EnvironmentId,alert.Severity,rule.Metric,rule.Expression,"Triggered",transition.OccurredAt,settings.ConsoleBaseUrl??new Uri("https://unconfigured.invalid/"));
                    var delivery=Create(alert,transition,channel,target,id,payload,frozen.Policy,now,reason,null);db.Add(delivery);rows.Add(delivery);
                }
            }
            return;
        }
        if(transition.ToStatus!="Resolved"&&transition.ToStatus!="Silenced"&&transition.FromStatus!="Silenced")return;
        var tasks=await RowsAsync(alert.Id,ct);
        if(transition.ToStatus=="Silenced")
        {foreach(var row in tasks.Where(x=>x.TriggeredDeliveryId is null&&unstarted.Contains(x.Status))){row.Status="Paused";row.Reason="Silenced";row.Revision++;}return;}
        if(transition.FromStatus=="Silenced"&&transition.ToStatus!="Resolved")
        {
            foreach(var row in tasks.Where(x=>x.Status=="Paused"&&x.TriggeredDeliveryId is null))
            {var reason=await UnavailableReasonAsync(alert,row.Channel,row.ProfileId,row.Target,ct);if(row.ExpiresAt<=now)End(row,"Expired","DeadlineExceeded",now);else if(reason is not null)End(row,"Suppressed",reason,now);else {row.Status=row.NextAttemptAt>now?"RetryScheduled":"Queued";row.Reason=null;row.Revision++;}}
            return;
        }
        if(transition.ToStatus=="Resolved")
        {
            var frozen=Snapshot(alert);alert.FrozenNotification=Encoding.UTF8.GetString(CanonicalJson.Serialize(frozen with{ClosureKind=governance?"Governance":transition.ActorId is null?"Recovery":"Manual"}));
            await ResolveLockedAsync(alert,transition,tasks,now,ct);
        }
    }
    private async Task<bool> ResolveLockedAsync(AlertEvent alert,AlertEventTransition transition,List<NotificationDelivery> tasks,DateTimeOffset now,CancellationToken ct)
    {
        var frozen=Snapshot(alert);var changed=false;var governance=frozen.ClosureKind=="Governance";
        foreach(var row in tasks.Where(x=>x.TriggeredDeliveryId is null&&unstarted.Contains(x.Status)))
        {End(row,"Suppressed",governance?transition.Reason:"EventResolved",now);changed=true;}
        if(governance||!frozen.Policy.ExternalEnabled||!frozen.Policy.NotifyRecovery)return changed;
        var parents=tasks.Where(x=>x.TriggeredDeliveryId is null&&x.Status!="Sending").ToArray();var ids=parents.Select(x=>x.Id).ToArray();var accepted=await db.Set<NotificationDeliveryAttempt>().AsNoTracking().Where(x=>ids.Contains(x.DeliveryId)&&(x.Outcome=="Accepted"||x.Outcome=="OutcomeUnknown")).Select(x=>x.DeliveryId).Distinct().ToArrayAsync(ct);
        foreach(var parent in parents.Where(x=>x.Status=="Accepted"||accepted.Contains(x.Id)))
        {
            if(tasks.Any(x=>x.TransitionId==transition.Id&&x.Channel==parent.Channel&&x.TargetHash==parent.TargetHash))continue;
            var reason=transition.FromStatus=="Silenced"?"SuppressedSilenced":await UnavailableReasonAsync(alert,parent.Channel,parent.ProfileId,parent.Target,ct);
            var original=JsonSerializer.Deserialize<NotificationMessageV1>(parent.Payload,CanonicalJson.Options)??throw new ApiException(503,"notification_payload_invalid","通知消息不可用。");
            var payload=NotificationPayload.Serialize(original with{Transition="Resolved",OccurredAt=transition.OccurredAt});var delivery=Create(alert,transition,parent.Channel,parent.Target,parent.ProfileId,payload,frozen.Policy,now,reason,parent.Id);
            if(reason is null&&transition.ActorId is not null)delivery.Reason="ManualClosure";
            db.Add(delivery);tasks.Add(delivery);changed=true;
        }
        return changed;
    }
    private async Task<string?> UnavailableReasonAsync(AlertEvent alert,string channel,Guid? profileId,string target,CancellationToken ct)
    {
        var rule=await db.Set<AlertRule>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==alert.RuleId,ct);
        if(rule is null||!rule.Enabled||rule.LogicRevision!=alert.LogicRevision)return "RuleInactive";
        if(!(await scopes.ResolveForSystemAsync(rule,ct)).Contains(alert.EnvironmentId))return "ScopeInactive";
        var state=await db.Set<NotificationChannelState>().AsNoTracking().SingleOrDefaultAsync(x=>x.Channel==channel,ct);
        if(state is null||!state.Enabled)return "ChannelDisabled";
        if(profileId is null||state.ProfileId!=profileId)return "ProfileChanged";
        var profile=await db.Set<NotificationChannelProfile>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==profileId,ct);if(profile is null)return "ProfileUnavailable";
        try
        {
            var config=NotificationConfigurationService.Decode(profile);
            if(channel=="Email"){settings.RequireAllowedRecipient(target);settings.RequireAllowedEndpoint(NotificationChannel.Email,config.Host!,config.Port!.Value);}
            else {var url=new Uri(target);settings.RequireAllowedEndpoint(NotificationChannel.Webhook,url.IdnHost,url.Port,url);}
        }
        catch(ApiException){return "TargetNotAllowed";}
        return null;
    }
    private async Task<List<NotificationDelivery>> RowsAsync(Guid id,CancellationToken ct)
    {
        var persisted=await db.Set<NotificationDelivery>().FromSqlInterpolated($"SELECT * FROM notification_deliveries WHERE event_id={id} ORDER BY id FOR UPDATE").ToListAsync(ct);
        foreach(var row in db.ChangeTracker.Entries<NotificationDelivery>().Where(x=>x.State==EntityState.Added&&x.Entity.EventId==id).Select(x=>x.Entity))if(!persisted.Any(x=>x.Id==row.Id))persisted.Add(row);
        return persisted;
    }
    private static NotificationFrozenSnapshot Snapshot(AlertEvent alert)
    {
        var snapshot=JsonSerializer.Deserialize<NotificationFrozenSnapshot>(alert.FrozenNotification,CanonicalJson.Options)??throw new ApiException(503,"notification_snapshot_invalid","通知冻结策略不可用。");
        if(snapshot.ClosureKind is not(null or "Governance" or "Recovery" or "Manual"))throw new ApiException(503,"notification_snapshot_invalid","通知冻结策略不可用。");
        return snapshot with{Policy=NotificationPolicyValidator.Normalize(snapshot.Policy)};
    }
    private static string TargetHash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static NotificationDelivery Create(AlertEvent alert,AlertEventTransition transition,string channel,string target,Guid? profile,byte[] payload,NotificationIntent policy,DateTimeOffset now,string? reason,Guid? parent)
    {
        var retry=policy.RetryPolicy!;
        return new(){EventId=alert.Id,TransitionId=transition.Id,TriggeredDeliveryId=parent,OrganizationId=alert.OrganizationId,ProjectId=alert.ProjectId,EnvironmentId=alert.EnvironmentId,Channel=channel,Target=target,TargetHash=TargetHash(target),ProfileId=profile,Payload=payload,MaxAttempts=retry.MaxAttempts,BaseDelaySeconds=retry.BaseDelaySeconds,MaxDelaySeconds=retry.MaxDelaySeconds,ExpiresAfterMinutes=retry.ExpiresAfterMinutes,CreatedAt=now,ExpiresAt=now.AddMinutes(retry.ExpiresAfterMinutes),Status=reason is null?"Queued":"Suppressed",Reason=reason,CompletedAt=reason is null?null:now};
    }
    private static void End(NotificationDelivery row,string status,string reason,DateTimeOffset now)
    {row.Status=status;row.Reason=reason;row.CompletedAt=now;row.NextAttemptAt=null;row.Revision++;}
    public async Task CoordinateResolvedAsync(Guid eventId,CancellationToken ct=default)=>await CoordinateAsync(eventId,ct);
    private async Task<bool> CoordinateAsync(Guid eventId,CancellationToken ct)
    {
        // Called after attempt writeback/commit, with a fresh context. Never
        // acquire upstream locks while a caller holds a delivery lock.
        if(db.Database.CurrentTransaction is not null)throw new InvalidOperationException("Recovery coordination owns its transaction.");
        db.ChangeTracker.DetectChanges();if(db.ChangeTracker.Entries().Any(x=>x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))throw new InvalidOperationException("Recovery coordination requires a saved context.");db.ChangeTracker.Clear();
        var metadata=await db.Set<AlertEvent>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==eventId,ct);if(metadata is null)return false;
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_rules WHERE id={metadata.RuleId} FOR UPDATE",ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_evaluation_states WHERE rule_id={metadata.RuleId} AND logic_revision={metadata.LogicRevision} AND environment_id={metadata.EnvironmentId} AND resource_key={metadata.ResourceKey} FOR UPDATE",ct);
        var alert=await db.Set<AlertEvent>().FromSqlInterpolated($"SELECT * FROM alert_events WHERE id={eventId} FOR UPDATE").SingleAsync(ct);
        if(alert.Status!="Resolved"){await tx.CommitAsync(ct);return false;}
        var transition=await db.Set<AlertEventTransition>().AsNoTracking().Where(x=>x.EventId==eventId&&x.ToStatus=="Resolved").OrderBy(x=>x.OccurredAt).ThenBy(x=>x.Id).FirstOrDefaultAsync(ct);if(transition is null){await tx.CommitAsync(ct);return false;}
        var changed=await ResolveLockedAsync(alert,transition,await RowsAsync(eventId,ct),await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct),ct);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return changed;
    }
    public async Task<bool> CoordinateNextResolvedAsync(CancellationToken ct=default)
    {
        var candidates=await db.Set<AlertEvent>().AsNoTracking().Where(e=>e.Status=="Resolved"&&EF.Functions.JsonContains(e.FrozenNotification,"{\"policy\":{\"externalEnabled\":true,\"notifyRecovery\":true}}")&&!EF.Functions.JsonContains(e.FrozenNotification,"{\"closureKind\":\"Governance\"}")&&db.Set<NotificationDelivery>().Any(p=>p.EventId==e.Id&&p.TriggeredDeliveryId==null&&(p.Status=="Sending"||p.Status=="Accepted"||db.Set<NotificationDeliveryAttempt>().Any(a=>a.DeliveryId==p.Id&&(a.Outcome=="Accepted"||a.Outcome=="OutcomeUnknown")))&&!db.Set<NotificationDelivery>().Any(child=>child.TriggeredDeliveryId==p.Id))).OrderBy(e=>e.ResolvedAt).ThenBy(e=>e.Id).Select(e=>e.Id).Take(50).ToArrayAsync(ct);
        var changed=false;foreach(var id in candidates)changed=await CoordinateAsync(id,ct)||changed;return changed;
    }
}

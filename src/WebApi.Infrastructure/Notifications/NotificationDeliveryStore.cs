using WebApi.Contracts.Notifications;
using WebApi.Contracts.Common;
using WebApi.Domain.Notifications;
using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
namespace WebApi.Infrastructure.Notifications;
public sealed record DeliveryLease(Guid Id,long Token,int AttemptNo,DateTimeOffset ExpiresAt);
internal sealed record NotificationReadiness(string? Status,string? Reason);
public sealed class NotificationDeliveryStore(WebApiDbContext db,NotificationDeploymentSettings settings,INotificationSecretResolver secrets,NotificationSecretVersion versions,AlertRuleScopeResolver scopes,SystemSettingsReader reader)
{
    private static readonly string[] unfinished=["Queued","RetryScheduled","Paused","Sending"];
    internal async Task<NotificationDelivery?> LockAsync(Guid id,CancellationToken ct)
    {
        var metadata=await db.Set<NotificationDelivery>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(metadata is null)return null;
        if(metadata.EventId is {} eventId)
        {
            var alert=await db.Set<AlertEvent>().AsNoTracking().SingleAsync(x=>x.Id==eventId,ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_rules WHERE id={alert.RuleId} FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_evaluation_states WHERE rule_id={alert.RuleId} AND logic_revision={alert.LogicRevision} AND environment_id={alert.EnvironmentId} AND resource_key={alert.ResourceKey} FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_events WHERE id={eventId} FOR UPDATE",ct);
        }
        return await db.Set<NotificationDelivery>().FromSqlInterpolated($"SELECT * FROM notification_deliveries WHERE id={id} FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(ct);
    }
    internal async Task<NotificationReadiness> ReadinessAsync(NotificationDelivery row,CancellationToken ct)
    {
        if(row.ProfileId is null)return new("Suppressed","ProfileUnavailable");
        var profile=await db.Set<NotificationChannelProfile>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==row.ProfileId&&x.Channel==row.Channel,ct);if(profile is null)return new("Suppressed","ProfileUnavailable");
        if(row.Kind=="Alert")
        {
            var alert=await db.Set<AlertEvent>().AsNoTracking().SingleAsync(x=>x.Id==row.EventId,ct);var rule=await db.Set<AlertRule>().AsNoTracking().SingleAsync(x=>x.Id==alert.RuleId,ct);
            if(!rule.Enabled||rule.LogicRevision!=alert.LogicRevision)return new("Suppressed","RuleInactive");
            if(!(await scopes.ResolveForSystemAsync(rule,ct)).Contains(alert.EnvironmentId))return new("Suppressed","ScopeInactive");
            if(alert.Status=="Resolved"&&row.TriggeredDeliveryId is null)return new("Suppressed","EventResolved");
            if(alert.Status=="Silenced")return new("Paused","Silenced");
            var state=await db.Set<NotificationChannelState>().AsNoTracking().SingleOrDefaultAsync(x=>x.Channel==row.Channel,ct);
            if(state is null||!state.Enabled)return new("Suppressed","ChannelDisabled");
            if(state.ProfileId!=row.ProfileId)return new("Suppressed","ProfileChanged");
        }
        else
        {
            var saved=await reader.ReadAsync("notification",ct);if(!saved.IsSaved)return new("Suppressed","TestConfigurationChanged");
            try {if(NotificationConfigurationService.Hash(NotificationConfigurationService.Configuration((NotificationSettings)saved.Value,Enum.Parse<NotificationChannel>(row.Channel)))!=profile.ConfigurationHash)return new("Suppressed","TestConfigurationChanged");}
            catch(ApiException){return new("Suppressed","TestConfigurationChanged");}
        }
        try {await EnvelopeAsync(row,profile,ct);return new(null,null);}
        catch(ApiException error)when(error.Code=="notification_protection_unavailable"){return new("Paused","ProtectionUnavailable");}
        catch(ApiException error)when(error.Code=="notification_secret_unavailable"){return new("Paused","SecretUnavailable");}
        catch(ApiException error)when(error.Code=="notification_secret_version_changed"){return new("Suppressed","SecretVersionChanged");}
        catch(ApiException){return new("Suppressed","TargetNotAllowed");}
    }
    private async Task<NotificationSendEnvelope> EnvelopeAsync(NotificationDelivery row,NotificationChannelProfile profile,CancellationToken ct)
    {
        var configuration=NotificationConfigurationService.Decode(profile);var channel=Enum.Parse<NotificationChannel>(row.Channel);
        var secret=await secrets.ResolveAsync(configuration.Reference,channel,ct);
        if(!versions.Verify(profile.Id,profile.ProtectedSecretFingerprint,secret))throw new ApiException(422,"notification_secret_version_changed","通知秘密版本已变化。");
        if(channel==NotificationChannel.Email)
        {settings.RequireAllowedRecipient(row.Target);settings.RequireAllowedEndpoint(channel,configuration.Host!,configuration.Port!.Value);return new(row.Id,row.Payload,configuration.Host!,configuration.Port.Value,secret,configuration.Security,configuration.FromEmail,row.Target);}
        var url=new Uri(row.Target);settings.RequireAllowedEndpoint(channel,url.IdnHost,url.Port,url);return new(row.Id,row.Payload,url.IdnHost,url.Port,secret,url:url);
    }
    public async Task<DeliveryLease?> TryBeginAsync(string owner,CancellationToken ct=default)
    {
        if(owner.Length is <1 or >128||owner.Any(char.IsControl))throw new ArgumentException("Invalid notification owner.");
        if(db.Database.CurrentTransaction is not null)throw new InvalidOperationException("Notification claims own their transaction.");
        var observed=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);
        var candidates=await db.Set<NotificationDelivery>().AsNoTracking().Where(x=>(x.Status=="Queued"||x.Status=="RetryScheduled")&&(x.NextAttemptAt==null||x.NextAttemptAt<=observed)||unfinished.Contains(x.Status)&&x.ExpiresAt<=observed||x.Status=="Sending"&&x.LeaseUntil<=observed).OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).Select(x=>x.Id).Take(settings.MaxClaimsPerPoll).ToArrayAsync(ct);
        foreach(var id in candidates)
        {
            db.ChangeTracker.Clear();await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);var row=await LockAsync(id,ct);var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);
            if(row is null||!unfinished.Contains(row.Status)){await tx.CommitAsync(ct);continue;}
            if(row.Status=="Sending")
            {
                if(row.LeaseUntil>now&&row.ExpiresAt>now){await tx.CommitAsync(ct);continue;}
                var attempt=await db.Set<NotificationDeliveryAttempt>().SingleAsync(x=>x.DeliveryId==row.Id&&x.AttemptNo==row.AttemptCount&&x.LeaseToken==row.LeaseToken,ct);
                if(attempt.CompletedAt is null){attempt.CompletedAt=now;attempt.Outcome="OutcomeUnknown";attempt.Code="LeaseExpired";}
                row.LeaseToken++;ClearLease(row);var readiness=await ReadinessAsync(row,ct);ApplyResult(row,new(DeliveryOutcome.OutcomeUnknown,"LeaseExpired"),now,readiness);Audit(row,"LeaseExpired",now);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);continue;
            }
            if(row.ExpiresAt<=now){Stop(row,"Expired","DeadlineExceeded",now);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);continue;}
            if(row.AttemptCount>=row.MaxAttempts){Stop(row,"Failed","AttemptsExhausted",now);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);continue;}
            if(row.Status=="Paused"||row.NextAttemptAt>now){await tx.CommitAsync(ct);continue;}
            var available=await ReadinessAsync(row,ct);
            if(available.Status is not null){Stop(row,available.Status,available.Reason!,now);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);continue;}
            row.Status="Sending";row.Reason=null;row.CompletedAt=null;row.NextAttemptAt=null;row.AttemptCount++;row.LeaseToken++;row.LeaseOwner=owner;row.LeaseUntil=now.AddSeconds(settings.LeaseSeconds);row.Revision++;
            db.Add(new NotificationDeliveryAttempt{DeliveryId=row.Id,AttemptNo=row.AttemptCount,LeaseToken=row.LeaseToken,StartedAt=now});Audit(row,"Sending",now);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(row.Id,row.LeaseToken,row.AttemptCount,row.ExpiresAt);
        }
        return null;
    }
    public async Task<NotificationSendEnvelope?> PrepareAsync(DeliveryLease lease,CancellationToken ct=default)
    {
        var row=await db.Set<NotificationDelivery>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==lease.Id&&x.Status=="Sending"&&x.LeaseToken==lease.Token&&x.AttemptCount==lease.AttemptNo,ct);
        var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);if(row is null||row.LeaseUntil<=now||row.ExpiresAt<=now)return null;
        var profile=await db.Set<NotificationChannelProfile>().AsNoTracking().SingleAsync(x=>x.Id==row.ProfileId,ct);
        // Begin/commit is the send-start point. Later silence/disable stops future
        // attempts, while this attempt still uses its original immutable profile.
        return await EnvelopeAsync(row,profile,ct);
    }
    public async Task<bool> CompleteAsync(DeliveryLease lease,TransportResult result,CancellationToken ct=default)
    {
        if(db.Database.CurrentTransaction is not null)throw new InvalidOperationException("Notification completion owns its transaction.");
        db.ChangeTracker.Clear();await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);var row=await LockAsync(lease.Id,ct);var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);
        if(row is null||row.Status!="Sending"||row.LeaseToken!=lease.Token||row.AttemptCount!=lease.AttemptNo||row.LeaseUntil<=now){await tx.CommitAsync(ct);return false;}
        var attempt=await db.Set<NotificationDeliveryAttempt>().SingleAsync(x=>x.DeliveryId==row.Id&&x.AttemptNo==lease.AttemptNo&&x.LeaseToken==lease.Token,ct);if(attempt.CompletedAt is not null){await tx.CommitAsync(ct);return false;}
        if(!Enum.IsDefined(result.Outcome)||result.Code.Length is <1 or >64||!System.Text.RegularExpressions.Regex.IsMatch(result.Code,"^[A-Za-z][A-Za-z0-9]{0,63}$")||result.ProtocolStatus is not(null or >=100 and <=599))throw new ArgumentException("Invalid notification transport result.");
        attempt.CompletedAt=now;attempt.Outcome=result.Outcome.ToString();attempt.Code=result.Code;attempt.ProtocolStatus=result.ProtocolStatus;ClearLease(row);var readiness=result.Outcome==DeliveryOutcome.Accepted?new NotificationReadiness(null,null):await ReadinessAsync(row,ct);ApplyResult(row,result,now,readiness);Audit(row,result.Code,now);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;
    }
    private static void ClearLease(NotificationDelivery row){row.LeaseOwner=null;row.LeaseUntil=null;}
    private static void Stop(NotificationDelivery row,string status,string reason,DateTimeOffset now)
    {ClearLease(row);row.Status=status;row.Reason=reason;row.CompletedAt=status=="Paused"?null:now;row.Revision++;}
    private static void ApplyResult(NotificationDelivery row,TransportResult result,DateTimeOffset now,NotificationReadiness available)
    {
        if(result.Outcome==DeliveryOutcome.Accepted){Stop(row,"Accepted",result.Code,now);row.NextAttemptAt=null;return;}
        if(result.Outcome==DeliveryOutcome.PermanentFailure){Stop(row,"Failed",result.Code,now);row.NextAttemptAt=null;return;}
        if(row.ExpiresAt<=now){Stop(row,"Expired","DeadlineExceeded",now);row.NextAttemptAt=null;return;}
        var decision=NotificationDecisionMachine.NextAttemptAt(new(row.MaxAttempts,row.BaseDelaySeconds,row.MaxDelaySeconds,row.ExpiresAfterMinutes),row.AttemptCount,now,row.ExpiresAt,result.RetryAfter,Random.Shared.NextDouble()*.2);row.NextAttemptAt=decision.NextAt;
        if(decision.Status!=DeliveryStatus.RetryScheduled){Stop(row,decision.Status.ToString(),decision.Reason!,now);return;}
        if(available.Status is not null){Stop(row,available.Status,available.Reason!,now);return;}
        row.Status="RetryScheduled";row.Reason=result.Code;row.CompletedAt=null;row.Revision++;
    }
    private void Audit(NotificationDelivery row,string reason,DateTimeOffset now)=>db.Add(new AuditLog{OrganizationId=row.OrganizationId,ProjectId=row.ProjectId,EnvironmentId=row.EnvironmentId,Action="notification.delivery",ResourceType="NotificationDelivery",ResourceId=row.Id.ToString("D"),TraceId="notification:"+row.Id.ToString("N"),AfterJson=System.Text.Json.JsonSerializer.Serialize(new{channel=row.Channel,status=row.Status,attemptCount=row.AttemptCount,reason}),CreatedAt=now});
}

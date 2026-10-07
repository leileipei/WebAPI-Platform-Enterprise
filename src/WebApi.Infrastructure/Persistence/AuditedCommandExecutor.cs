using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Domain.Notifications;
namespace WebApi.Infrastructure.Persistence;
public sealed record AuditRequestMetadata(System.Net.IPAddress? Ip);
public sealed class AuditedCommandExecutor(WebApiDbContext db,AuditRequestMetadata? metadata=null)
{
    private static readonly HashSet<string> fields=["Id","Code","Name","Status","DisplayName","Revision","EnvironmentId","ProjectId","OrganizationId","Version","Path","NormalizedPath","Methods","Enabled","TimeoutMs","ApplicationId","ApiId","ConfigVersion","DeploymentSequence","ReleaseNo","RoleId","UserId","PermissionId","AccessMode","ValidFrom","ExpiresAt","ReleaseId","ApiVersionId","ClusterId","CreatedBy","AssigneeUserId","StepOrder","ActedAt","Priority","Weight","OwnerUserId","LifecycleStatus","ReleaseType","RollbackOf","RecoveryOf","FromConfigVersion","ToConfigVersion","DeadlineAt","FailureCode","Metric","Expression","Severity","ForSeconds","WindowSeconds","TargetType","TargetId","LogicRevision","RuleId","RuleRevision","ResourceKey","ResourceType","ResourceId","OccurrenceNo","ResolvedBy","ResolveReason","ResolvedAt","AckedBy","AckedAt","SilencedBy","SilencedUntil","SilenceReason","FromStatus","ToStatus","ActorId","Reason","OccurredAt","CorrelationId","Phase","PendingSince","SuppressedAt","LastEventId"];
    public async Task<T> ExecuteAsync<T>(ActorContext actor,ScopeRef scope,string action,Func<WebApiDbContext,CancellationToken,Task<T>> command,CancellationToken cancellationToken=default)
    {
        var owned=db.Database.CurrentTransaction is null?await db.Database.BeginTransactionAsync(cancellationToken):null;
        try
        {
            // All authorization-changing commands and protected writes share this transaction lock.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",cancellationToken);
            if(!await db.Set<UserRecord>().AsNoTracking().AnyAsync(x=>x.Id==actor.UserId && x.Status=="Active",cancellationToken)) throw new ApiException(401,"inactive_session","会话已失效。");
            var value=await command(db,cancellationToken);
            db.ChangeTracker.DetectChanges();
            var changes=db.ChangeTracker.Entries().Where(e=>e.Entity is not AuditLog && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
            var first=changes.FirstOrDefault(e=>e.Entity is SsoProvider or SsoProviderTest)??changes.FirstOrDefault(e=>e.Entity is SystemSetting)??changes.FirstOrDefault(e=>e.Entity is not IdempotencyRecord);
            if(changes.Length>0) db.Add(new AuditLog {
                UserId=actor.UserId,OrganizationId=scope.OrganizationId==Guid.Empty?null:scope.OrganizationId,ProjectId=scope.ProjectId,EnvironmentId=scope.EnvironmentId,
                Action=action,ResourceType=first?.Entity is SsoProviderTest?nameof(SsoProvider):first?.Metadata.ClrType.Name??action.Split('.')[0],
                ResourceId=first?.Entity is SsoProviderTest test?test.ProviderId.ToString():first?.Properties.FirstOrDefault(p=>p.Metadata.IsPrimaryKey())?.CurrentValue?.ToString()??"",
                BeforeJson=JsonSerializer.Serialize(changes.Where(e=>e.State!=EntityState.Added).Select(e=>Capture(e,true))),
                AfterJson=JsonSerializer.Serialize(changes.Where(e=>e.State!=EntityState.Deleted).Select(e=>Capture(e,false))),TraceId=actor.TraceId,Ip=metadata?.Ip
            });
            await db.SaveChangesAsync(cancellationToken);
            if(owned is not null) await owned.CommitAsync(cancellationToken);
            return value;
        }
        catch {if(owned is not null) await owned.RollbackAsync(CancellationToken.None);throw;}
        finally {if(owned is not null) await owned.DisposeAsync();}
    }
    private static object Capture(EntityEntry e,bool original)
    {
        var captured=e.Properties.Where(p=>fields.Contains(p.Metadata.Name)||(e.Entity is Policy&&p.Metadata.Name is "Type" or "VersionNo")).ToDictionary(p=>p.Metadata.Name,p=>original?p.OriginalValue:p.CurrentValue);
        if(e.Entity is AlertRule)
        {
            captured.Remove("Notification");var property=e.Property(nameof(AlertRule.Notification));var json=(string?)(original?property.OriginalValue:property.CurrentValue);
            if(json is not null)
            {
                var policy=NotificationPolicyValidator.Parse(json);captured["NotificationHash"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(policy)));
                captured["NotificationSummary"]=new{policy.ExternalEnabled,policy.RequestedChannels,recipientCount=policy.EmailRecipients?.Count??0,policy.NotifyRecovery,policy.RetryPolicy};
                captured["ChangedFields"]=e.Properties.Where(p=>!Equals(p.OriginalValue,p.CurrentValue)).Select(p=>p.Metadata.Name).ToArray();
            }
        }
        if(e.Entity is NotificationChannelProfile)
            foreach(var name in new[]{"Channel","ConfigurationHash","SettingsRevision"})captured[name]=original?e.Property(name).OriginalValue:e.Property(name).CurrentValue;
        if(e.Entity is NotificationChannelState)
            foreach(var name in new[]{"Channel","ProfileId"})captured[name]=original?e.Property(name).OriginalValue:e.Property(name).CurrentValue;
        if(e.Entity is NotificationDelivery)
            foreach(var name in new[]{"Channel","Kind","AttemptCount"})captured[name]=original?e.Property(name).OriginalValue:e.Property(name).CurrentValue;
        if(e.Entity is SsoProvider)
        {
            foreach(var name in new[]{"AuthRevision","IsDefault","ProviderType"})captured[name]=original?e.Property(name).OriginalValue:e.Property(name).CurrentValue;
            var configFields=new[]{"Issuer","ClientId","OrganizationId","SecretRef","ScopesJson","ClaimMappingJson"};
            var config=configFields.ToDictionary(name=>name,name=>original?e.Property(name).OriginalValue:e.Property(name).CurrentValue);
            captured["ConfigHash"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(config)));
            captured["HasSecretReference"]=!string.IsNullOrEmpty((string?)config["SecretRef"]);
            captured["ChangedFields"]=e.Properties.Where(p=>!Equals(p.OriginalValue,p.CurrentValue)).Select(p=>p.Metadata.Name).ToArray();
        }
        if(e.Entity is SsoProviderTest)
        {
            captured["ProviderId"]=original?e.Property("ProviderId").OriginalValue:e.Property("ProviderId").CurrentValue;
            captured["ProviderRevision"]=original?e.Property("ProviderRevision").OriginalValue:e.Property("ProviderRevision").CurrentValue;
        }
        if(e.Entity is UserExternalIdentity)
        {
            captured["ProviderId"]=original?e.Property("ProviderId").OriginalValue:e.Property("ProviderId").CurrentValue;
            captured["ChangedFields"]=e.Properties.Where(p=>!Equals(p.OriginalValue,p.CurrentValue)).Select(p=>p.Metadata.Name).ToArray();
        }
        if(e.Entity is UserRecord user&&user.AuthSource=="sso")
        {
            captured.Remove("DisplayName");captured["AuthSource"]="sso";
            captured["ChangedFields"]=e.Properties.Where(p=>!Equals(p.OriginalValue,p.CurrentValue)&&p.Metadata.Name is not ("PasswordHash" or "SecurityStamp")).Select(p=>p.Metadata.Name).ToArray();
        }
        if(e.Entity is SystemSetting setting) {
            var prop=e.Property(nameof(SystemSetting.Value));var json=(string?)(original?prop.OriginalValue:prop.CurrentValue)??"{}";
            using var doc=JsonDocument.Parse(json);captured["Key"]=setting.Key;
            captured["ValueHash"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(doc.RootElement)));
            using var old=JsonDocument.Parse((string?)prop.OriginalValue??"{}");using var next=JsonDocument.Parse((string?)prop.CurrentValue??"{}");
            captured["ChangedFields"]=next.RootElement.EnumerateObject().Where(p=>!old.RootElement.TryGetProperty(p.Name,out var before)||before.GetRawText()!=p.Value.GetRawText()).Select(p=>p.Name).ToArray();
            if(setting.Key=="system.notification")foreach(var key in new[]{"smtpSecretRef","webhookSecretRef"}) {
                var reference=doc.RootElement.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
                captured[key]=new {hasConfiguredReference=reference is not null,provider=WebApi.Infrastructure.Settings.SystemSettingsValidator.ReferenceProvider(reference)};
            }
        }
        if(e.Entity is Policy) {
            var property=e.Property(nameof(Policy.Config));var config=(string?)(original?property.OriginalValue:property.CurrentValue);
            // Persist a hash of policy configuration rather than arbitrary legacy JSON in audit output.
            if(config is null) captured["ConfigHash"]=null;
            else {using var document=JsonDocument.Parse(config);captured["ConfigHash"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(document.RootElement)));}
        }
        if(e.Entity is ApiImportPreview)
            foreach(var name in new[]{"SourceHash","BundleHash","SourceFormat","Dialect","SourcePolicyRevision","ImportId"})captured[name]=original?e.Property(name).OriginalValue:e.Property(name).CurrentValue;
        if(e.Entity is ApiVersionContractSources)
            foreach(var name in new[]{"BundleHash","Dialect","SourcePolicyRevision"})captured[name]=original?e.Property(name).OriginalValue:e.Property(name).CurrentValue;
        if(e.Entity is ProjectImportSourcePolicy){
            var property=e.Property(nameof(ProjectImportSourcePolicy.RulesJson));var json=(string?)(original?property.OriginalValue:property.CurrentValue);
            if(json is null)captured["RulesHash"]=null;
            else{using var document=JsonDocument.Parse(json);captured["RulesHash"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(document.RootElement)));}
        }
        if(e.Entity is ApiVersionComparison comparison)
        {captured["ApiId"]=comparison.ApiId;captured["FromVersionId"]=comparison.FromVersionId;captured["ToVersionId"]=comparison.ToVersionId;captured["FromRevision"]=comparison.FromRevision;captured["ToRevision"]=comparison.ToRevision;captured["EngineVersion"]=comparison.EngineVersion;captured["InputFingerprint"]=comparison.InputFingerprint;captured["ReportHash"]=comparison.ReportHash;captured["Coverage"]=comparison.Coverage;captured["Counts"]=JsonSerializer.Deserialize<JsonElement>(comparison.CountsJson);}
        if(e.Entity is ApiVersionRiskReview review)
        {captured["ComparisonId"]=review.ComparisonId;captured["ApiId"]=review.ApiId;captured["Decision"]=review.Decision;captured["InputFingerprint"]=review.InputFingerprint;captured["ReportHash"]=review.ReportHash;}
        return new {Type=e.Metadata.ClrType.Name,Fields=captured};
    }
}

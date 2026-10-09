using WebApi.Infrastructure.Governance;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Settings;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Notifications;
namespace WebApi.Infrastructure.Settings;
public sealed class SystemSettingsService(WebApiDbContext db,AuthorizationService auth,SystemSettingsReader reader,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,SettingsPreviewProtector protector,TimeProvider clock,ScopeResolver scopes,NotificationConfigurationService notifications)
{
    private static readonly ScopeRef platform=new(Guid.Empty);
    private Task Require(ActorContext actor,CancellationToken ct)=>auth.RequireAsync(actor,"system.manage",new("system_settings",Guid.Empty,platform),ct);
    public async Task<IReadOnlyList<SettingsGroupDto>> ListAsync(ActorContext actor,CancellationToken ct=default){await Require(actor,ct);var groups=new List<SettingsGroupDto>();foreach(var group in SystemSettingsValidator.Groups)groups.Add(View(group,await reader.ReadAsync(group,ct)));return groups;}
    public async Task<SettingsGroupDto> GetAsync(string group,ActorContext actor,CancellationToken ct=default){await Require(actor,ct);return View(group,await reader.ReadAsync(group,ct));}
    public async Task<SettingsValidationDto> ValidateAsync(string group,SettingsMutation request,ActorContext actor,CancellationToken ct=default){await Require(actor,ct);var stored=await reader.ReadAsync(group,ct);SystemSettingsValidator.Resolve(group,request,stored.Value);return new("StructuralOnly","结构校验通过；未解析 SecretRef，未连接 SMTP/Webhook，未发送通知。");}
    public async Task<SettingsPreviewDto> PreviewAsync(string group,SettingsMutation request,string? tag,ActorContext actor,CancellationToken ct=default){await Require(actor,ct);var current=await reader.ReadAsync(group,ct);PolicyPreconditions.Require(tag,current.Revision);var validated=SystemSettingsValidator.Resolve(group,request,current.Value);var before=SafeJson(current.Value);var after=SafeJson(validated.Value);return new(validated.ChangedFields.Select(f=>new SettingsDifference(f,PreviewDisplay(group,f,before.GetProperty(f)),PreviewDisplay(group,f,after.GetProperty(f)))).ToArray(),SystemSettingsValidator.Fields[group].Select(Effect).Distinct().ToArray(),protector.Issue(actor.UserId,group,current.Revision,validated.ValueHash),clock.GetUtcNow().AddMinutes(5));}
    public Task<CommandResult<SettingsGroupDto>> SaveAsync(string group,SaveSettingsRequest request,string? tag,ActorContext actor,CancellationToken ct=default)=>commands.ExecuteAsync(actor,platform,"settings.system."+group+".update",async(_,token)=>{
        await Require(actor,token);SettingsValueCodec.Default(group);
        return await idempotency.ExecuteAsync(new(actor.UserId,platform,"settings.system."+group,requestContext.IdempotencyKey),CanonicalJson.Serialize(new{group,values=request.Values,tag}),async commandToken=>{
            var current=await reader.ReadAsync(group,commandToken);PolicyPreconditions.Require(tag,current.Revision);var validated=SystemSettingsValidator.Resolve(group,new(request.Values),current.Value);protector.Require(request.ConfirmationToken,actor.UserId,group,current.Revision,validated.ValueHash);
            var key="system."+group;var row=await db.Set<SystemSetting>().SingleOrDefaultAsync(x=>x.Key==key,commandToken);if(row is null){row=new SystemSetting{Key=key};db.Add(row);}else row.Revision++;
            if(group=="notification")await notifications.ApplyAsync((NotificationSettings)current.Value,(NotificationSettings)validated.Value,row.Revision,actor,commandToken);
            row.Value=validated.CanonicalValueJson;row.UpdatedBy=actor.UserId;row.UpdatedAt=clock.GetUtcNow();return new CommandResult<SettingsGroupDto>(View(group,new(validated.Value,row.Revision,true)),RevisionTag.Format(row.Revision));
        },token);
    },ct);
    public async Task<RouteDefaultsDto> RouteDefaultsAsync(Guid environmentId,ActorContext actor,CancellationToken ct=default){var scope=await scopes.EnvironmentAsync(environmentId,ct);await auth.RequireAsync(actor,"route.write",new("environment",environmentId,scope),ct);var defaults=await reader.ReadAsync("gateway",ct);return new(((GatewaySettings)defaults.Value).DefaultRouteTimeoutMs,defaults.IsSaved?"Saved":"CodeDefault");}
    private static string PreviewDisplay(string group,string field,JsonElement value)=>group=="notification"&&field=="fromEmail"?(value.ValueKind==JsonValueKind.Null?"未配置":"已配置发件地址"):Display(value);
    private static string Display(JsonElement value)=>value.ValueKind==JsonValueKind.String?value.GetString()!:value.GetRawText();
    public static JsonElement SafeJson(SettingsValues value)
    {
        if(value is not NotificationSettings n)return SettingsValueCodec.Json(value);
        return JsonSerializer.SerializeToElement(new{n.SmtpHost,n.SmtpPort,n.FromEmail,smtpSecretRef=new SecretReferenceState(n.SmtpSecretRef is not null,SystemSettingsValidator.ReferenceProvider(n.SmtpSecretRef)),n.WebhookUrl,webhookSecretRef=new SecretReferenceState(n.WebhookSecretRef is not null,SystemSettingsValidator.ReferenceProvider(n.WebhookSecretRef)),n.SmtpEnabled,n.WebhookEnabled,n.SmtpSecurity},CanonicalJson.Options);
    }
    public static string Effect(string field)=>field switch{"loginIpMaxAttempts" or "loginIpWindowSeconds" or "loginAccountMaxAttempts" or "loginAccountWindowSeconds"=>"LoginRequest","sessionTtlMinutes"=>"NewLogin","passwordMinLength" or "passwordComplexity"=>"NewUser","defaultRouteTimeoutMs"=>"NewRoute","auditExportEnabled"=>"ExportRequest","maxRequestBodyMb" or "configRefreshIntervalSeconds"=>"DeploymentRequired","productionApprovalLevels"=>"FixedConstraint","smtpEnabled" or "webhookEnabled"=>"AutomaticNotifications","smtpSecurity"=>"NewChannelProfile",_=>"ConfigurationIntent"};
    private static SettingsGroupDto View(string group,SettingsStoredValue value)=>new(group,value.Revision,SafeJson(value.Value),SystemSettingsValidator.Fields[group].Select(field=>new SettingFieldMeta(field,value.IsSaved?"Saved":field is "snapshotRetentionCount" or "maxRequestBodyMb" or "auditRetentionDays"?"PrototypeSuggestion":"CodeDefault",Effect(field))).ToArray());
}

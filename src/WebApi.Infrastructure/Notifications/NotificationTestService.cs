using WebApi.Contracts.Notifications;
using WebApi.Contracts.Security;
using WebApi.Contracts.Common;
using WebApi.Domain.Notifications;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Settings;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Alerts;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Security.Cryptography;
namespace WebApi.Infrastructure.Notifications;
public sealed class NotificationTestService(WebApiDbContext db,AuthorizationService authorization,SystemSettingsReader reader,NotificationConfigurationService profiles,NotificationDeploymentSettings settings,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,NotificationQueryService queries)
{
    private Task Require(ActorContext actor,CancellationToken ct)=>authorization.RequireAsync(actor,"system.manage",new("notification_test",Guid.Empty,NotificationQueryService.Platform),ct);
    public async Task<NotificationDeploymentPolicyDto> DeploymentPolicyAsync(ActorContext actor,CancellationToken ct=default)
    {await Require(actor,ct);return new(settings.AllowedRecipients,settings.AllowedDomains,"DeploymentConfiguration",20);}
    public Task<NotificationDeliveryDto> CreateAsync(NotificationChannel channel,string? email,string tag,ActorContext actor,CancellationToken ct=default)=>commands.ExecuteAsync(actor,NotificationQueryService.Platform,"notification.test.create",async(_,token)=>{
        await Require(actor,token);if(!Enum.IsDefined(channel)||channel==NotificationChannel.Webhook&&email is not null)throw new ApiException(422,"invalid_notification_test","测试渠道及收件目标不合法。");
        var target=channel==NotificationChannel.Email?NotificationPolicyValidator.NormalizeEmail(email??""):null;if(target is not null)settings.RequireAllowedRecipient(target);
        return await idempotency.ExecuteAsync(new(actor.UserId,NotificationQueryService.Platform,"notification.test.create",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{channel,email=target,tag}),async inner=>{
            var saved=await reader.ReadAsync("notification",inner);if(!saved.IsSaved)throw new ApiException(409,"notification_not_saved","请先保存通知配置。");PolicyPreconditions.Require(tag,saved.Revision);if(settings.ConsoleBaseUrl is null)throw new ApiException(422,"notification_console_unavailable","通知控制台地址尚未配置。");var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,inner);var name=channel.ToString();
            if(await db.Set<NotificationDelivery>().AnyAsync(x=>x.Kind=="Test"&&x.CreatedBy==actor.UserId&&x.Channel==name&&x.CreatedAt>now.AddMinutes(-1),inner))throw new ApiException(429,"notification_test_rate_limited","每位用户每渠道一分钟内只能创建一次测试。");
            if(await db.Set<NotificationDelivery>().CountAsync(x=>x.Kind=="Test"&&(x.Status=="Queued"||x.Status=="Sending"||x.Status=="RetryScheduled"||x.Status=="Paused"),inner)>=10)throw new ApiException(429,"notification_test_capacity","平台尚未结束的测试达到10条，请等待回执。");
            var profile=await profiles.CreateProfileAsync((NotificationSettings)saved.Value,channel,saved.Revision,actor.UserId,inner);target??=NotificationConfigurationService.Decode(profile).Url!;
            var row=new NotificationDelivery{Kind="Test",CreatedBy=actor.UserId,Channel=name,Target=target,TargetHash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target))),ProfileId=profile.Id,Payload=NotificationPayload.Test(settings.ConsoleBaseUrl,now),MaxAttempts=1,ExpiresAfterMinutes=5,CreatedAt=now,ExpiresAt=now.AddMinutes(5)};db.Add(row);return NotificationQueryService.Dto(row);
        },token);
    },ct);
    public async Task<NotificationDeliveryDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {await Require(actor,ct);var row=await db.Set<NotificationDelivery>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.Kind=="Test",ct)??throw ScopeResolver.Missing();return NotificationQueryService.Dto(row,await queries.CanRetryAsync(row,actor,await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct),ct));}
}

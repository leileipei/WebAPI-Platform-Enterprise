using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Infrastructure.Notifications;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class NotificationApiTests
{
    [Fact] public async Task DisabledChannelCanBeTestedWithoutActivation()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync(enabled:false);string settings;await using(var db=scenario.Api.Context())settings=(await db.Set<SystemSetting>().SingleAsync(x=>x.Key=="system.notification")).Value;using var created=await scenario.TestAsync();Assert.Equal(HttpStatusCode.Accepted,created.StatusCode);var dto=(await created.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!;Assert.Equal(DeliveryStatus.Queued,dto.Status);Assert.Equal(1,dto.MaxAttempts);Assert.Null(dto.EventId);Assert.True(created.Headers.CacheControl?.NoStore);Assert.DoesNotContain("recipient@example.test",await created.Content.ReadAsStringAsync());
        await using(var db=scenario.Api.Context()){Assert.Equal(1,await db.Set<NotificationDelivery>().CountAsync());Assert.Equal(settings,(await db.Set<SystemSetting>().SingleAsync(x=>x.Key=="system.notification")).Value);Assert.All(await db.Set<NotificationChannelState>().ToArrayAsync(),x=>{Assert.False(x.Enabled);Assert.Null(x.ProfileId);});}
        using(var services=scenario.Api.Services())Assert.True(await services.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync("test-dispatch"));using var receipt=await scenario.Api.Client.GetAsync("/api/v1/settings/system/notification/tests/"+dto.Id);Assert.True(receipt.Headers.CacheControl?.NoStore);Assert.Equal(DeliveryStatus.Accepted,(await receipt.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!.Status);Assert.True(Assert.Single(scenario.Transport.Smtp.Receipts).Tls);
    }
    [Fact] public async Task TestLimitsAndIdempotencyAreAtomic()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync(enabled:false);var key=Guid.NewGuid().ToString("N");using var first=await scenario.TestAsync(key:key);using var replay=await scenario.TestAsync(key:key);Assert.Equal(HttpStatusCode.Accepted,first.StatusCode);Assert.Equal(HttpStatusCode.Accepted,replay.StatusCode);Assert.Equal((await first.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!.Id,(await replay.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!.Id);using var again=await scenario.TestAsync();Assert.Equal(HttpStatusCode.TooManyRequests,again.StatusCode);await using var read=scenario.Api.Context();Assert.Equal(1,await read.Set<NotificationDelivery>().CountAsync());Assert.Equal(1,await read.Set<IdempotencyRecord>().CountAsync(x=>x.Operation.Contains("notification.test")));
    }
    [Theory][InlineData(null,428)][InlineData("\"0\"",412)][InlineData("bad",412)]
    public async Task TestRequiresSavedRevisionPrecondition(string? tag,int status)
    {await using var scenario=new NotificationScenario();await scenario.InitializeAsync(enabled:false);using var response=await scenario.TestAsync(tag:tag);Assert.Equal((HttpStatusCode)status,response.StatusCode);await using var read=scenario.Api.Context();Assert.Empty(await read.Set<NotificationDelivery>().ToArrayAsync());}
    [Fact] public async Task UnknownAndCrossScopeDeliveryIdsAre404()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();var e=await scenario.AlertAsync();using var unknown=await scenario.Api.Client.GetAsync($"/api/v1/notification-deliveries/{Guid.NewGuid()}/attempts");Assert.Equal(HttpStatusCode.NotFound,unknown.StatusCode);await using(var db=scenario.Api.Context()) {db.RemoveRange(await db.Set<UserProjectScope>().Where(x=>x.UserId==scenario.Api.User.Id).ToArrayAsync());await db.SaveChangesAsync();}using var denied=await scenario.Api.Client.GetAsync($"/api/v1/alerts/{e}/notifications");Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
    }
    [Fact] public async Task RetryCannotResetBudgetOrShortenRetryAfter()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();var e=await scenario.AlertAsync();Guid id;DateTimeOffset deadline,next;await using(var db=scenario.Api.Context()){var row=await db.Set<NotificationDelivery>().SingleAsync();id=row.Id;row.Status="RetryScheduled";row.AttemptCount=1;row.LeaseToken=1;var clock=DateTimeOffset.UtcNow.AddSeconds(1200);next=new DateTimeOffset(clock.Ticks/10*10,TimeSpan.Zero);row.NextAttemptAt=next;deadline=row.ExpiresAt;db.Add(new NotificationDeliveryAttempt{DeliveryId=id,AttemptNo=1,LeaseToken=1,StartedAt=row.CreatedAt,CompletedAt=DateTimeOffset.UtcNow,Outcome="TransientFailure",Code="WebhookTemporaryFailure",ProtocolStatus=429});await db.SaveChangesAsync();}
        using var retry=await scenario.Api.WriteAsync(HttpMethod.Post,$"/api/v1/notification-deliveries/{id}/retry",new{},"\"1\"");retry.EnsureSuccessStatusCode();await using var read=scenario.Api.Context();var original=await read.Set<NotificationDelivery>().SingleAsync();Assert.Equal(1,original.AttemptCount);Assert.Equal(deadline,original.ExpiresAt);Assert.Equal(next,original.NextAttemptAt);Assert.Equal(1,await read.Set<NotificationDeliveryAttempt>().CountAsync());var dto=(await retry.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!;Assert.Equal(1,dto.AttemptCount);Assert.Equal(next,dto.NextAttemptAt);Assert.Equal(DeliveryStatus.RetryScheduled,dto.Status);
    }
    [Fact] public async Task LimitsUseRealHierarchyPermissionAndNeverExposeTargets()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();using var limits=await scenario.Api.Client.GetAsync($"/api/v1/notification-limits?organizationId={scenario.Api.Organization.Id}&projectId={scenario.Api.Project.Id}&environmentId={scenario.Api.Environment.Id}");limits.EnsureSuccessStatusCode();Assert.True(limits.Headers.CacheControl?.NoStore);var dto=(await limits.Content.ReadFromJsonAsync<NotificationLimitsDto>())!;Assert.Equal(20,dto.MaxRecipients);Assert.True(dto.EmailConfigured&&dto.EmailEnabled&&dto.WebhookConfigured&&dto.WebhookEnabled);var text=await limits.Content.ReadAsStringAsync();Assert.DoesNotContain("fixture.test",text);Assert.DoesNotContain("vault",text);
        using var mismatch=await scenario.Api.Client.GetAsync($"/api/v1/notification-limits?organizationId={scenario.Api.Organization.Id}&projectId={Guid.NewGuid()}&environmentId={scenario.Api.Environment.Id}");Assert.Equal(HttpStatusCode.NotFound,mismatch.StatusCode);
    }
    [Theory]
    [InlineData("{\"channel\":\"Webhook\",\"url\":\"https://unapproved.example.test/\"}")]
    [InlineData("{\"channel\":\"Email\",\"Channel\":\"Webhook\",\"email\":\"recipient@example.test\"}")]
    public async Task TestRejectsUnknownOrDuplicateFieldsWithoutCreatingJobs(string json)
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync(enabled:false);using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/settings/system/notification/tests"){Content=new StringContent(json,Encoding.UTF8,"application/json")};request.Headers.Add("X-CSRF-Token",await scenario.Api.CsrfAsync());request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));request.Headers.Add("If-Match","\"1\"");using var response=await scenario.Api.Client.SendAsync(request);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);await using var read=scenario.Api.Context();Assert.Empty(await read.Set<NotificationDelivery>().ToArrayAsync());
    }
    [Fact] public async Task ConcurrentTestCreationCannotExceedPlatformCapacity()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync(enabled:false);using var created=await scenario.TestAsync();created.EnsureSuccessStatusCode();
        var (_,reviewer)=await scenario.Api.NewReviewerAsync("TestOperator");await using(var db=scenario.Api.Context())
        {
            var actorRole=await db.Set<UserRole>().SingleAsync(x=>x.UserId==scenario.Api.User.Id);var reviewerUser=(await db.Set<UserRecord>().SingleAsync(x=>x.Username.StartsWith("reviewer_"))).Id;db.Add(new UserRole{UserId=reviewerUser,RoleId=actorRole.RoleId});
            var original=await db.Set<NotificationDelivery>().SingleAsync();original.CreatedAt=DateTimeOffset.UtcNow.AddMinutes(-2);original.ExpiresAt=original.CreatedAt.AddMinutes(5);
            for(var i=0;i<8;i++)db.Add(new NotificationDelivery{Kind="Test",CreatedBy=original.CreatedBy,Channel=original.Channel,Target=original.Target,TargetHash=original.TargetHash,ProfileId=original.ProfileId,Payload=original.Payload,MaxAttempts=1,ExpiresAfterMinutes=5,CreatedAt=original.CreatedAt,ExpiresAt=original.ExpiresAt});await db.SaveChangesAsync();
        }
        var responses=await Task.WhenAll(scenario.TestAsync(),scenario.TestAsync(client:reviewer));try{Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.Accepted);var limited=Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.TooManyRequests);Assert.Contains("notification_test_capacity",await limited.Content.ReadAsStringAsync());await using var db=scenario.Api.Context();Assert.Equal(10,await db.Set<NotificationDelivery>().CountAsync());}finally{foreach(var response in responses)response.Dispose();}
    }
    [Fact] public async Task WebhookTestUsesRealSignedTlsTransportAndReceiptsDoNotExposeBodyOrTarget()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync(enabled:false);using var created=await scenario.TestAsync(NotificationChannel.Webhook);created.EnsureSuccessStatusCode();var dto=(await created.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!;using(var services=scenario.Api.Services())Assert.True(await services.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync("signed-hook"));
        var remote=Assert.Single(scenario.Transport.Webhook.Receipts);Assert.Equal(dto.Id,remote.DeliveryId);Assert.True(remote.SignatureValid&&remote.Tls&&remote.RemoteAccepted);using var response=await scenario.Api.Client.GetAsync($"/api/v1/notification-deliveries/{dto.Id}/attempts");response.EnsureSuccessStatusCode();Assert.True(response.Headers.CacheControl?.NoStore);var attempts=(await response.Content.ReadFromJsonAsync<PageResult<NotificationAttemptDto>>())!;Assert.Equal(DeliveryOutcome.Accepted,Assert.Single(attempts.Items).Outcome);var text=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("hook.fixture.test",text);Assert.DoesNotContain("vault://",text);Assert.DoesNotContain("consoleLink",text);
    }
    [Theory][InlineData("{\"target\":\"changed@example.test\"}")][InlineData("{\"attemptCount\":0}")]
    public async Task RetryRejectsAdditionalBodyFields(string body)
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();await scenario.AlertAsync();Guid id;await using(var db=scenario.Api.Context()){var row=await db.Set<NotificationDelivery>().SingleAsync();id=row.Id;row.Status="RetryScheduled";row.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(300);await db.SaveChangesAsync();}
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/notification-deliveries/{id}/retry"){Content=new StringContent(body,Encoding.UTF8,"application/json")};request.Headers.Add("X-CSRF-Token",await scenario.Api.CsrfAsync());request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));request.Headers.Add("If-Match","\"1\"");using var response=await scenario.Api.Client.SendAsync(request);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);await using var read=scenario.Api.Context();Assert.Equal(1,(await read.Set<NotificationDelivery>().SingleAsync()).Revision);
    }

    [Fact] public async Task RetryReplayReauthorizesAndStaleTagDoesNotConsumeTheKey()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();await scenario.AlertAsync();Guid id;await using(var db=scenario.Api.Context()){var row=await db.Set<NotificationDelivery>().SingleAsync();id=row.Id;row.Status="RetryScheduled";row.NextAttemptAt=DateTimeOffset.UtcNow.AddMinutes(20);await db.SaveChangesAsync();}
        var key=Guid.NewGuid().ToString("N");async Task<HttpResponseMessage> Send(string tag){using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/notification-deliveries/{id}/retry"){Content=JsonContent.Create(new{})};request.Headers.Add("X-CSRF-Token",await scenario.Api.CsrfAsync());request.Headers.Add("Idempotency-Key",key);request.Headers.Add("If-Match",tag);return await scenario.Api.Client.SendAsync(request);}
        using var stale=await Send("\"0\"");Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);using var valid=await Send("\"1\"");valid.EnsureSuccessStatusCode();using var replay=await Send("\"1\"");replay.EnsureSuccessStatusCode();Assert.Equal((await valid.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!.Revision,(await replay.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!.Revision);
        await using(var db=scenario.Api.Context()){var permission=await db.Set<Permission>().SingleAsync(x=>x.Code=="alert.operate");db.RemoveRange(await db.Set<RolePermission>().Where(x=>x.PermissionId==permission.Id).ToArrayAsync());await db.SaveChangesAsync();}
        using var denied=await Send("\"1\"");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);using var visible=await scenario.Api.Client.GetAsync($"/api/v1/notification-deliveries/{id}/attempts");visible.EnsureSuccessStatusCode();await using var read=scenario.Api.Context();Assert.Equal(2,(await read.Set<NotificationDelivery>().SingleAsync()).Revision);
    }
    [Theory][InlineData("page=0")][InlineData("pageSize=101")][InlineData("page=1&page=2")][InlineData("page=abc")]
    public async Task ReceiptPaginationRejectsInvalidAndDuplicateParameters(string query)
    {await using var scenario=new NotificationScenario();await scenario.InitializeAsync();var e=await scenario.AlertAsync();using var response=await scenario.Api.Client.GetAsync($"/api/v1/alerts/{e}/notifications?{query}");Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);}

    [Fact] public async Task OnlyPlatformManagersCanReadDeploymentRecipientPolicy()
    {
        await using var scenario=new NotificationScenario();await scenario.InitializeAsync();using var response=await scenario.Api.Client.GetAsync("/api/v1/settings/system/notification/deployment-policy");response.EnsureSuccessStatusCode();Assert.True(response.Headers.CacheControl?.NoStore);var text=await response.Content.ReadAsStringAsync();Assert.Contains("example.test",text);Assert.Contains("DeploymentConfiguration",text);Assert.DoesNotContain("vault://",text);Assert.DoesNotContain("smtp.json",text);
        var (_,reader)=await scenario.Api.NewReviewerAsync("PolicyReader");using var denied=await reader.GetAsync("/api/v1/settings/system/notification/deployment-policy");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.DoesNotContain("example.test",await denied.Content.ReadAsStringAsync());
    }

}

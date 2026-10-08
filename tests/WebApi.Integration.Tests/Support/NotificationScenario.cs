using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Notifications;
using WebApi.Infrastructure.Notifications;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
namespace WebApi.Integration.Tests.Support;
internal sealed class NotificationScenario:IAsyncDisposable
{
    public ApiFixture Api {get;}=new();
    public NotificationTransportFixture Transport {get;}=new();
    private sealed class Dns:INotificationDnsResolver
    {public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct)=>Task.FromResult(new[]{IPAddress.Loopback});}
    public async Task InitializeAsync(bool enabled=true,Action<WebApplicationBuilder>? configure=null)
    {
        await Transport.InitializeAsync();await Api.InitializeAsync(builder=>{Transport.ConfigureNotifications(builder.Configuration);builder.Configuration["DataProtection:KeysDirectory"]=Path.Combine(Path.GetDirectoryName(Transport.RootCertificatePath)!,"keys");builder.Configuration["DataProtection:ApplicationName"]="NotificationScenario";builder.Services.AddSingleton<INotificationDnsResolver,Dns>();configure?.Invoke(builder);});await Api.SeedCatalogAsync();await ObservationTestSupport.GrantAsync(Api,["alert.read","alert.operate","alert.rule.manage"]);await SystemSettingsFixture.PromoteAsync(Api);using var login=await Api.LoginAsync();login.EnsureSuccessStatusCode();
        using var saved=await SystemSettingsFixture.SaveAsync(Api,"notification",new {smtpHost="smtp.fixture.test",smtpPort=Transport.Smtp.Port,fromEmail="sender@example.test",smtpSecretRef=new{operation="Replace",reference="vault://fixture/smtp"},webhookUrl=Transport.WebhookUrl.AbsoluteUri,webhookSecretRef=new{operation="Replace",reference="vault://fixture/webhook"},smtpEnabled=enabled,webhookEnabled=enabled,smtpSecurity="StartTlsRequired"});saved.EnsureSuccessStatusCode();
    }
    public async Task<Guid> AlertAsync(IReadOnlyList<string>? channels=null)
    {
        using var saved=await Api.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",new SaveAlertRuleRequest(Api.Organization.Id,Api.Project.Id,Api.Environment.Id,"Notify "+Guid.NewGuid().ToString("N"),"request_rps","request_rps > 1","Warning",true,0,"Environment",null,60,new(true,channels??["Email"],true,["recipient@example.test"],true,new(5,1,900,1440))));saved.EnsureSuccessStatusCode();var rule=(await saved.Content.ReadFromJsonAsync<AlertRuleDto>())!;
        using var services=Api.Services();var db=services.ServiceProvider.GetRequiredService<WebApiDbContext>();await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)");await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_rules WHERE id={rule.Id} FOR UPDATE");var e=new AlertEvent{RuleId=rule.Id,RuleRevision=1,LogicRevision=1,OrganizationId=Api.Organization.Id,ProjectId=Api.Project.Id,EnvironmentId=Api.Environment.Id,ResourceType="Environment",ResourceKey="Environment",OccurrenceNo=1,Message="Threshold",RuleSummary="request_rps > 1"};var transition=new AlertEventTransition{EventId=e.Id,ToStatus="Open",Reason="Triggered"};db.AddRange(e,transition);await services.ServiceProvider.GetRequiredService<NotificationPlanner>().OnTransitionAsync(e,transition);await db.SaveChangesAsync();await tx.CommitAsync();return e.Id;
    }
    public async Task<HttpResponseMessage> TestAsync(NotificationChannel channel=NotificationChannel.Email,string? tag="\"1\"",string? key=null,HttpClient? client=null)
    {
        client??=Api.Client;var csrf=await client.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf");using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/settings/system/notification/tests"){Content=JsonContent.Create(new CreateNotificationTestRequest(channel,channel==NotificationChannel.Email?"recipient@example.test":null))};request.Headers.Add("X-CSRF-Token",csrf!["token"]);request.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString("N"));if(tag is not null)request.Headers.TryAddWithoutValidation("If-Match",tag);return await client.SendAsync(request);
    }
    public async ValueTask DisposeAsync(){await Api.DisposeAsync();await Transport.DisposeAsync();}
}

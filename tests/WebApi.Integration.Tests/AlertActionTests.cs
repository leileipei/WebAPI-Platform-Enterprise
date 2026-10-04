using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Npgsql;
using WebApi.Contracts.Alerts;
using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class AlertActionTests
{
    private static async Task<(ApiFixture Fixture,Guid Id,AlertClockedMetricHandler Handler)> Setup()
    {
        var f=new ApiFixture();var handler=new AlertClockedMetricHandler();await f.InitializeAsync(builder=>{builder.Configuration["Observability:Enabled"]="true";builder.Services.AddSingleton(new AlertEvaluationSettings(IntervalSeconds:1,QueryDelaySeconds:0));builder.Services.AddScoped<AlertEvaluationLeaseStore>();builder.Services.AddScoped<AlertEvaluationService>();builder.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(b=>b.PrimaryHandler=handler));});
        await f.SeedCatalogAsync();await ObservationTestSupport.GrantAsync(f,["alert.read","alert.operate","alert.rule.manage"]);await using(var db=f.Context()){f.Application.OrganizationId=f.Organization.Id;f.Application.ProjectId=f.Project.Id;db.Add(f.Application);db.AddRange(new GatewayNode{EnvironmentId=f.Environment.Id,NodeName="node-0"},new GatewayNode{EnvironmentId=f.Environment.Id,NodeName="node-1"});await db.SaveChangesAsync();}
        handler.Inner.EnvironmentId=f.Environment.Id;handler.Inner.ApiId=f.Api.Id;handler.Inner.ApplicationId=f.Application.Id;handler.Inner.DestinationId=f.Destination.Id;handler.Inner.ClusterId=f.Cluster.Id;
        using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();using var rule=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",new SaveAlertRuleRequest(f.Organization.Id,f.Project.Id,f.Environment.Id,"Rule","request_rps","request_rps > 1","Warning",true,0,"Environment",null,60,new(true,[])));rule.EnsureSuccessStatusCode();var definition=(await rule.Content.ReadFromJsonAsync<AlertRuleDto>())!;
        var e=new AlertEvent{RuleId=definition.Id,RuleRevision=1,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,ResourceType="Environment",ResourceKey="Environment",OccurrenceNo=1,Message="threshold exceeded",RuleSummary="Safe rule",EvaluationState="Known",LastCondition=true,LastValue=3,LastObservedAt=DateTimeOffset.UtcNow.AddSeconds(-3)};
        await using(var db=f.Context()){db.Add(e);db.Add(new AlertEvaluationState{RuleId=definition.Id,LogicRevision=1,OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,EnvironmentId=f.Environment.Id,ResourceType="Environment",ResourceKey="Environment",Phase="Firing",LastCondition=true,LastSuccessAt=e.LastObservedAt,LastEventId=e.Id,NextOccurrenceNo=2,EvaluationState="Known"});await db.SaveChangesAsync();}return(f,e.Id,handler);
    }
    private static async Task<HttpResponseMessage> Send(ApiFixture f,Guid id,AlertAction action,string tag,string? key=null)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/observability/alerts/{id}/{action.Kind.ToLowerInvariant()}"){Content=JsonContent.Create(action)};request.Headers.Add("X-CSRF-Token",await f.CsrfAsync());request.Headers.Add("If-Match",tag);request.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString("N"));return await f.Client.SendAsync(request);
    }
    private static async Task<AlertEventDto> Action(ApiFixture f,Guid id,AlertAction action,string tag){using var r=await Send(f,id,action,tag);r.EnsureSuccessStatusCode();return(await r.Content.ReadFromJsonAsync<AlertEventDto>())!;}
    private static async Task<int> Expire(ApiFixture f)
    {
        using var scope=f.Services();var type=typeof(AlertRuleService).Assembly.GetType("WebApi.Infrastructure.Alerts.AlertSilenceExpiryService");Assert.NotNull(type);var service=scope.ServiceProvider.GetRequiredService(type);return await(Task<int>)type.GetMethod("ExpireAsync")!.Invoke(service,[CancellationToken.None])!;
    }
    private static async Task<bool> Evaluate(ApiFixture f)
    {
        using var scope=f.Services();var store=scope.ServiceProvider.GetRequiredService<AlertEvaluationLeaseStore>();var lease=Assert.Single(await store.ClaimAsync(await store.CurrentSlotAsync(default),"action-test",default));return await scope.ServiceProvider.GetRequiredService<AlertEvaluationService>().EvaluateAsync(lease,default);
    }
    [Fact] public async Task SilenceAckThenExpiryRestoresAck()
    {
        var s=await Setup();await using var f=s.Fixture;var silent=await Action(f,s.Id,new("Silence","maintenance",null,900),"\"1\"");Assert.Equal("Silenced",silent.Status);Assert.True(silent.SilencedUntil>DateTimeOffset.UtcNow.AddMinutes(14.9));
        var ack=await Action(f,s.Id,new("Ack",null,null),$"\"{silent.Revision}\"");Assert.Equal("Silenced",ack.Status);Assert.Equal(f.User.Id,ack.AckedBy);
        await using(var db=f.Context())await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE alert_events SET silenced_until=clock_timestamp()-interval '1 second' WHERE id={s.Id}");
        var counts=await Task.WhenAll(Expire(f),Expire(f));Assert.Equal(1,counts.Sum());await using var read=f.Context();var expired=await read.Set<AlertEvent>().SingleAsync();Assert.Equal("Ack",expired.Status);Assert.Equal(3,await read.Set<AlertEventTransition>().CountAsync());Assert.Equal(1,await read.Set<AuditLog>().CountAsync(x=>x.Action=="alert.silence_expired"));
    }
    [Fact] public async Task ResolveSuppressesPersistentTrueUntilRecovery()
    {
        var s=await Setup();await using var f=s.Fixture;var resolved=await Action(f,s.Id,new("Resolve","handled",null),"\"1\"");Assert.Equal("Resolved",resolved.Status);Assert.Equal(f.User.Id,resolved.ResolvedBy);Assert.True(await Evaluate(f));
        await using(var db=f.Context()){Assert.Equal(1,await db.Set<AlertEvent>().CountAsync());Assert.Equal("SuppressedUntilRecovery",(await db.Set<AlertEvaluationState>().SingleAsync()).Phase);}
        await Task.Delay(3300);s.Handler.Inner.ResetWindow=true;Assert.True(await Evaluate(f));await using(var db=f.Context())Assert.Equal("Inactive",(await db.Set<AlertEvaluationState>().SingleAsync()).Phase);
        await Task.Delay(1100);s.Handler.Inner.ResetWindow=false;Assert.True(await Evaluate(f));await using var read=f.Context();var events=await read.Set<AlertEvent>().OrderBy(x=>x.OccurrenceNo).ToArrayAsync();Assert.Equal(2,events.Length);Assert.Equal("Resolved",events[0].Status);Assert.Equal("Open",events[1].Status);Assert.NotEqual(events[0].Id,events[1].Id);Assert.Equal(2,events[1].OccurrenceNo);
    }
    [Fact] public async Task ResolvedCannotReopen()
    {
        var s=await Setup();await using var f=s.Fixture;var silent=await Action(f,s.Id,new("Silence","maintenance",null,3600),"\"1\"");var closed=await Action(f,s.Id,new("Resolve","handled",null),$"\"{silent.Revision}\"");using var reopen=await Send(f,s.Id,new("Unsilence",null,null),$"\"{closed.Revision}\"");Assert.Equal(HttpStatusCode.Conflict,reopen.StatusCode);Assert.Equal(0,await Expire(f));await using var read=f.Context();Assert.Equal("Resolved",(await read.Set<AlertEvent>().SingleAsync()).Status);
    }
    [Fact] public async Task DuplicateKeyChangesOnlyOnce()
    {
        var s=await Setup();await using var f=s.Fixture;var key=Guid.NewGuid().ToString("N");var action=new AlertAction("Ack",null,null);using var first=await Send(f,s.Id,action,"\"1\"",key);first.EnsureSuccessStatusCode();using var retry=await Send(f,s.Id,action,"\"1\"",key);retry.EnsureSuccessStatusCode();await using var read=f.Context();Assert.Equal(1,await read.Set<AlertEventTransition>().CountAsync());Assert.Equal(2,(await read.Set<AlertEvent>().SingleAsync()).Revision);using var conflict=await Send(f,s.Id,action with{Reason="changed"},"\"1\"",key);Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);
    }
    [Fact] public async Task ScopeRevokedAtCommitBlocksAction()
    {
        var s=await Setup();await using var f=s.Fixture;await using var hold=f.Context();await using var tx=await hold.Database.BeginTransactionAsync();await hold.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)");hold.RemoveRange(await hold.Set<UserProjectScope>().Where(x=>x.UserId==f.User.Id).ToArrayAsync());await hold.SaveChangesAsync();var request=Send(f,s.Id,new("Ack",null,null),"\"1\"");
        await using(var c=await f.Database.OpenAsync()){var waited=false;var end=DateTimeOffset.UtcNow.AddSeconds(3);while(DateTimeOffset.UtcNow<end){await using var check=new NpgsqlCommand("SELECT count(*)::int FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory'",c);if((int)(await check.ExecuteScalarAsync())!>0){waited=true;break;}await Task.Delay(20);}Assert.True(waited,"Action must be waiting for the actual governance transaction.");}
        await tx.CommitAsync();using var denied=await request;Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);await using var read=f.Context();Assert.Equal("Open",(await read.Set<AlertEvent>().SingleAsync()).Status);Assert.Equal(0,await read.Set<AlertEventTransition>().CountAsync());
    }
    [Fact] public async Task AuditFailureRollsBackEventAndTransition()
    {
        var s=await Setup();await using var f=s.Fixture;await using(var db=f.Context())await db.Database.ExecuteSqlRawAsync("ALTER TABLE audit_logs ADD CONSTRAINT ck_test_alert_audit_failure CHECK (action <> 'alert.ack')");using var failed=await Send(f,s.Id,new("Ack",null,null),"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,failed.StatusCode);await using var read=f.Context();var rolled=await read.Set<AlertEvent>().SingleAsync();Assert.Equal(1,rolled.Revision);Assert.Equal("Open",rolled.Status);Assert.Equal(0,await read.Set<AlertEventTransition>().CountAsync());
    }
    [Fact] public async Task ReadOnlyScopeCanInspectButCannotOperate()
    {
        var s=await Setup();await using var f=s.Fixture;await ObservationTestSupport.GrantAsync(f,["alert.read","alert.operate"],"read");using var detail=await f.Client.GetAsync($"/api/v1/observability/alerts/{s.Id}");detail.EnsureSuccessStatusCode();using var action=await Send(f,s.Id,new("Ack",null,null),"\"1\"");Assert.Equal(HttpStatusCode.Forbidden,action.StatusCode);await using var read=f.Context();Assert.Equal(1,(await read.Set<AlertEvent>().SingleAsync()).Revision);Assert.Equal(0,await read.Set<AlertEventTransition>().CountAsync());
    }
    [Fact] public async Task ForeignEnvironmentIsHiddenAndFiltersUseFrozenScope()
    {
        var s=await Setup();await using var f=s.Fixture;Guid foreign;await using(var db=f.Context()){var env=new EnvironmentRecord{ProjectId=f.Project.Id,Code="FOREIGN",Name="Foreign"};db.Add(env);foreign=env.Id;var own=await db.Set<AlertEvent>().SingleAsync();db.Add(new AlertEvent{RuleId=own.RuleId,RuleRevision=1,LogicRevision=1,OccurrenceNo=1,OrganizationId=own.OrganizationId,ProjectId=own.ProjectId,EnvironmentId=env.Id,ResourceType="Environment",ResourceKey="Environment",Severity="Critical",RuleSummary="foreign-private-summary"});var grant=await db.Set<UserProjectScope>().SingleAsync(x=>x.UserId==f.User.Id);grant.ProjectId=f.Project.Id;grant.EnvironmentId=f.Environment.Id;await db.SaveChangesAsync();}
        Guid hidden;await using(var db=f.Context())hidden=await db.Set<AlertEvent>().Where(x=>x.EnvironmentId==foreign).Select(x=>x.Id).SingleAsync();using var detail=await f.Client.GetAsync($"/api/v1/observability/alerts/{hidden}");Assert.Equal(HttpStatusCode.NotFound,detail.StatusCode);Assert.DoesNotContain("foreign-private-summary",await detail.Content.ReadAsStringAsync());
        var url=$"/api/v1/observability/alerts?organizationId={f.Organization.Id}&projectId={f.Project.Id}&allAccessibleEnvironments=true&severity=Warning&source=Metrics&status=Open&pageSize=1";using var list=await f.Client.GetAsync(url);list.EnsureSuccessStatusCode();var page=(await list.Content.ReadFromJsonAsync<WebApi.Contracts.Common.PageResult<AlertEventDto>>())!;Assert.Equal(1,page.Total);Assert.Equal(s.Id,Assert.Single(page.Items).Id);using var invalid=await f.Client.GetAsync(url.Replace("source=Metrics","source=Webhook"));Assert.Equal(HttpStatusCode.UnprocessableEntity,invalid.StatusCode);
    }
    [Theory] [InlineData(900)] [InlineData(86400)]
    public async Task RelativeSilenceIncludesExactCommitBoundaries(int seconds)
    {
        var s=await Setup();await using var f=s.Fixture;var before=DateTimeOffset.UtcNow;var result=await Action(f,s.Id,new("Silence","boundary",null,seconds),"\"1\"");Assert.InRange(result.SilencedUntil!.Value,before.AddSeconds(seconds),DateTimeOffset.UtcNow.AddSeconds(seconds));Assert.Equal("Silenced",result.Status);
    }
    [Fact] public async Task InvalidReasonsDeadlinesAndStaleRevisionLeaveEventUntouched()
    {
        var s=await Setup();await using var f=s.Fixture;var invalid=new AlertAction[]{new("Resolve","  ",null),new("Silence",null,null,900),new("Silence","x",null,899),new("Silence","x",null,86401),new("Silence","x",DateTimeOffset.UtcNow.AddHours(1),900),new("Silence","x",DateTimeOffset.UtcNow.AddMinutes(14)),new("Ack",null,null,900)};
        foreach(var input in invalid){using var r=await Send(f,s.Id,input,"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);}using var stale=await Send(f,s.Id,new("Ack",null,null),"\"0\"");Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);using var unsilent=await Send(f,s.Id,new("Unsilence",null,null),"\"1\"");Assert.Equal(HttpStatusCode.Conflict,unsilent.StatusCode);await using var db=f.Context();Assert.Equal(1,(await db.Set<AlertEvent>().SingleAsync()).Revision);Assert.Equal(0,await db.Set<AlertEventTransition>().CountAsync());
    }
    [Fact] public async Task ExpiryWorkerRunsWhileMetricEvaluationIsActuallyBlocked()
    {
        var s=await Setup();await using var f=s.Fixture;await Action(f,s.Id,new("Silence","maintenance",null,900),"\"1\"");s.Handler.PauseFirst=true;using var services=f.Services();var factory=services.ServiceProvider.GetRequiredService<IServiceScopeFactory>();using var evaluate=new WebApi.Worker.Workers.AlertEvaluationWorker(factory,Microsoft.Extensions.Logging.Abstractions.NullLogger<WebApi.Worker.Workers.AlertEvaluationWorker>.Instance);using var expire=new WebApi.Worker.Workers.AlertSilenceExpiryWorker(factory,Microsoft.Extensions.Logging.Abstractions.NullLogger<WebApi.Worker.Workers.AlertSilenceExpiryWorker>.Instance);await evaluate.StartAsync(default);var restored=false;
        try{await s.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await using(var db=f.Context())await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE alert_events SET silenced_until=clock_timestamp()-interval '1 second' WHERE id={s.Id}");await expire.StartAsync(default);var end=DateTimeOffset.UtcNow.AddSeconds(3);while(DateTimeOffset.UtcNow<end){await using var db=f.Context();if(await db.Set<AlertEvent>().AnyAsync(x=>x.Id==s.Id&&x.Status=="Open")){restored=true;break;}await Task.Delay(25);}Assert.False(s.Handler.Resume.Task.IsCompleted);}
        finally{s.Handler.Resume.TrySetResult(true);await evaluate.StopAsync(default);await expire.StopAsync(default);}Assert.True(restored,"Expiry must not wait for a blocked external metrics query.");await using var read=f.Context();Assert.Equal(1,await read.Set<AuditLog>().CountAsync(x=>x.Action=="alert.silence_expired"));
    }
}

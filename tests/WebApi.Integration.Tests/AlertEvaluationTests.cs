using Microsoft.Extensions.Logging;
using WebApi.Worker;
using StackExchange.Redis;
using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Alerts;
using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class AlertEvaluationTests
{
    private static async Task<(ApiFixture Fixture,AlertClockedMetricHandler Handler,Guid Rule)> Setup(string target="Environment",int duration=0)
    {
        var handler=new AlertClockedMetricHandler();var fixture=new ApiFixture();
        await fixture.InitializeAsync(builder=>{builder.Configuration["Observability:Enabled"]="true";builder.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(b=>b.PrimaryHandler=handler));builder.Services.AddSingleton(new AlertEvaluationSettings(IntervalSeconds:1,QueryDelaySeconds:0));builder.Services.AddScoped<AlertEvaluationLeaseStore>();builder.Services.AddScoped<AlertEvaluationService>();});
        await fixture.SeedCatalogAsync();await ObservationTestSupport.GrantAsync(fixture,["alert.rule.manage"]);
        await using(var db=fixture.Context()){fixture.Application.OrganizationId=fixture.Organization.Id;fixture.Application.ProjectId=fixture.Project.Id;db.Add(fixture.Application);db.AddRange(new GatewayNode{EnvironmentId=fixture.Environment.Id,NodeName="node-0"},new GatewayNode{EnvironmentId=fixture.Environment.Id,NodeName="node-1"});await db.SaveChangesAsync();}
        handler.Inner.EnvironmentId=fixture.Environment.Id;handler.Inner.ApiId=fixture.Api.Id;handler.Inner.ApplicationId=fixture.Application.Id;handler.Inner.DestinationId=fixture.Destination.Id;handler.Inner.ClusterId=fixture.Cluster.Id;
        using var login=await fixture.LoginAsync();login.EnsureSuccessStatusCode();var request=new SaveAlertRuleRequest(fixture.Organization.Id,fixture.Project.Id,fixture.Environment.Id,"Rps","request_rps","request_rps > 1","Warning",true,duration,target,target=="Destination"?fixture.Destination.Id:null,60,new(true,[]));
        using var saved=await fixture.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",request);saved.EnsureSuccessStatusCode();return(fixture,handler,(await saved.Content.ReadFromJsonAsync<AlertRuleDto>())!.Id);
    }
    private static async Task<DateTimeOffset> Slot(ApiFixture f){using var scope=f.Services();return await scope.ServiceProvider.GetRequiredService<AlertEvaluationLeaseStore>().CurrentSlotAsync(default);}
    private static async Task<IReadOnlyList<EvaluationLease>> Claim(ApiFixture f,DateTimeOffset slot,string owner){using var scope=f.Services();return await scope.ServiceProvider.GetRequiredService<AlertEvaluationLeaseStore>().ClaimAsync(slot,owner,default);}
    private static async Task<bool> Evaluate(ApiFixture f,EvaluationLease lease){using var scope=f.Services();return await scope.ServiceProvider.GetRequiredService<AlertEvaluationService>().EvaluateAsync(lease,default);}
    private static async Task<EvaluationLease> Next(ApiFixture f){await Task.Delay(1100);var leases=await Claim(f,await Slot(f),"next");return Assert.Single(leases);}
    [Theory][InlineData("Ready")][InlineData("NotReady")][InlineData("Degraded")]
    public async Task RegisteredNodeStatusCanEvaluateActualRule(string status)
    {
        var setup=await Setup();await using var f=setup.Fixture;
        await using(var db=f.Context()){foreach(var node in await db.Set<GatewayNode>().ToArrayAsync())node.Status=status;await db.SaveChangesAsync();}
        Assert.True(await Evaluate(f,Assert.Single(await Claim(f,await Slot(f),"operational"))));
        await using var read=f.Context();Assert.Equal("Open",(await read.Set<AlertEvent>().SingleAsync()).Status);
    }
    [Fact] public async Task TwoWorkersSameSlotCreateOneEvent()
    {
        var setup=await Setup();await using var f=setup.Fixture;var slot=await Slot(f);var claimed=await Task.WhenAll(Claim(f,slot,"worker-a"),Claim(f,slot,"worker-b"));var lease=Assert.Single(claimed.SelectMany(x=>x));
        var commits=await Task.WhenAll(Evaluate(f,lease),Evaluate(f,lease));Assert.Single(commits,x=>x);await using var db=f.Context();var e=Assert.Single(await db.Set<AlertEvent>().ToArrayAsync());Assert.Equal("Open",e.Status);Assert.Equal(1,await db.Set<AlertEventTransition>().CountAsync());var audit=await db.Set<AuditLog>().SingleAsync(x=>x.Action=="alert.triggered");Assert.Null(audit.UserId);Assert.Equal(e.Id.ToString(),audit.ResourceId);Assert.Equal(f.Environment.Id,audit.EnvironmentId);Assert.Contains("worker",audit.AfterJson!);
    }
    [Fact] public async Task LeaseExpiredResultCannotCommitAfterDisable()
    {
        var setup=await Setup();await using var f=setup.Fixture;var lease=Assert.Single(await Claim(f,await Slot(f),"expired"));setup.Handler.PauseFirst=true;var running=Evaluate(f,lease);await setup.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await using(var db=f.Context())await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE alert_evaluation_states SET lease_until=clock_timestamp()-interval '1 second' WHERE rule_id={setup.Rule}");
            using var disable=await f.WriteAsync(HttpMethod.Post,$"/api/v1/observability/alert-rules/{setup.Rule}/disable",null,"\"1\"").WaitAsync(TimeSpan.FromSeconds(3));disable.EnsureSuccessStatusCode();
        }
        finally{setup.Handler.Resume.TrySetResult(true);}
        Assert.False(await running);await using var read=f.Context();Assert.Equal(0,await read.Set<AlertEvent>().CountAsync());
    }
    [Fact] public async Task ReclaimedTokenRejectsOldResultAndOnlyNewWorkerCommits()
    {
        var setup=await Setup();await using var f=setup.Fixture;var slot=await Slot(f);var old=Assert.Single(await Claim(f,slot,"old"));await using(var db=f.Context())await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE alert_evaluation_states SET lease_until=clock_timestamp()-interval '1 second' WHERE rule_id={setup.Rule}");
        var replacement=Assert.Single(await Claim(f,await Slot(f),"new"));Assert.True(replacement.Token>old.Token);Assert.False(await Evaluate(f,old));Assert.True(await Evaluate(f,replacement));await using var read=f.Context();Assert.Equal(1,await read.Set<AlertEvent>().CountAsync());
    }
    [Fact] public async Task RestartAndSourceFailureDoNotDuplicateOrRecoverFiringEvent()
    {
        var setup=await Setup();await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await Claim(f,await Slot(f),"before-restart"))));setup.Handler.Inner.Status=503;Assert.True(await Evaluate(f,await Next(f)));
        await using(var db=f.Context()){Assert.Equal("Open",(await db.Set<AlertEvent>().SingleAsync()).Status);Assert.Equal("Unknown",(await db.Set<AlertEvaluationState>().SingleAsync()).EvaluationState);}
        setup.Handler.Inner.Status=200;Assert.True(await Evaluate(f,await Next(f)));await using var read=f.Context();Assert.Equal(1,await read.Set<AlertEvent>().CountAsync());Assert.Equal(1,await read.Set<AuditLog>().CountAsync(x=>x.Action=="alert.triggered"));
    }
    [Fact] public async Task UnknownResetsPendingAndCpuClockRollbackCannotClaimPastSlot()
    {
        var setup=await Setup(duration:300);await using var f=setup.Fixture;var slot=await Slot(f);Assert.True(await Evaluate(f,Assert.Single(await Claim(f,slot,"pending"))));
        setup.Handler.Inner.Status=503;Assert.True(await Evaluate(f,await Next(f)));await using var read=f.Context();var state=await read.Set<AlertEvaluationState>().SingleAsync();Assert.Equal("Inactive",state.Phase);Assert.Null(state.PendingSince);Assert.Empty(await Claim(f,slot.AddSeconds(-1),"cpu-backward"));Assert.Equal(0,await read.Set<AlertEvent>().CountAsync());
    }
    [Fact] public async Task ResourceRetirementClosesActualEventWithoutBorrowingSourceAbsence()
    {
        var setup=await Setup("Destination");await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await Claim(f,await Slot(f),"active"))));
        await using(var db=f.Context()){db.Remove(await db.Set<UpstreamDestination>().SingleAsync(x=>x.Id==f.Destination.Id));await db.SaveChangesAsync();}
        var sourceCount=setup.Handler.Inner.Requests.Count;Assert.True(await Evaluate(f,await Next(f)));Assert.Equal(sourceCount,setup.Handler.Inner.Requests.Count);await using var read=f.Context();var e=await read.Set<AlertEvent>().SingleAsync();Assert.Equal("Resolved",e.Status);Assert.Equal("ResourceRetired",e.ResolveReason);Assert.Equal(2,await read.Set<AlertEventTransition>().CountAsync());
    }
    [Fact] public async Task ScopePauseKeepsFiringAndRecoveryResolvesAfterReactivation()
    {
        var setup=await Setup();await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await Claim(f,await Slot(f),"active"))));
        await using(var db=f.Context()){var env=await db.Set<EnvironmentRecord>().SingleAsync();env.Status="Inactive";await db.SaveChangesAsync();}
        Assert.True(await Evaluate(f,await Next(f)));await using(var db=f.Context()){var alert=await db.Set<AlertEvent>().SingleAsync();Assert.Equal("Open",alert.Status);Assert.Equal("ScopeInactive",alert.EvaluationState);Assert.Equal("Firing",(await db.Set<AlertEvaluationState>().SingleAsync()).Phase);var env=await db.Set<EnvironmentRecord>().SingleAsync();env.Status="Active";await db.SaveChangesAsync();}
        setup.Handler.Inner.ResetWindow=true;Assert.True(await Evaluate(f,await Next(f)));await using var read=f.Context();var recovered=await read.Set<AlertEvent>().SingleAsync();Assert.Equal("Resolved",recovered.Status);Assert.Equal("Recovered",recovered.ResolveReason);
    }
    [Fact] public async Task ScopePauseDoesNotEraseManualSuppression()
    {
        var setup=await Setup();await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await Claim(f,await Slot(f),"active"))));
        await using(var db=f.Context()){var state=await db.Set<AlertEvaluationState>().SingleAsync();var alert=await db.Set<AlertEvent>().SingleAsync();alert.Status="Resolved";alert.ResolveReason="Manual";alert.ResolvedAt=DateTimeOffset.UtcNow;state.Phase="SuppressedUntilRecovery";state.SuppressedAt=alert.ResolvedAt;var env=await db.Set<EnvironmentRecord>().SingleAsync();env.Status="Inactive";await db.SaveChangesAsync();}
        Assert.True(await Evaluate(f,await Next(f)));await using var read=f.Context();Assert.Equal("SuppressedUntilRecovery",(await read.Set<AlertEvaluationState>().SingleAsync()).Phase);Assert.NotNull((await read.Set<AlertEvaluationState>().SingleAsync()).SuppressedAt);
    }
    [Fact] public async Task WideRuleSeedsNewEnvironmentBeforeAnyOccurrenceExists()
    {
        var setup=await Setup(duration:300);await using var f=setup.Fixture;using var get=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var rule=(await get.Content.ReadFromJsonAsync<AlertRuleDto>())!;using var wide=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{setup.Rule}",rule.Definition with{EnvironmentId=null},"\"1\"");wide.EnsureSuccessStatusCode();
        Assert.True(await Evaluate(f,Assert.Single(await Claim(f,await Slot(f),"first"))));Guid added;
        await using(var db=f.Context()){var env=new EnvironmentRecord{ProjectId=f.Project.Id,Code="FUTURE",Name="Future"};added=env.Id;db.Add(env);await db.SaveChangesAsync();}
        await Task.Delay(1100);var leases=await Claim(f,await Slot(f),"future");Assert.Contains(leases,x=>x.EnvironmentId==added);foreach(var lease in leases)Assert.True(await Evaluate(f,lease));
        await using var read=f.Context();Assert.Equal(2,await read.Set<AlertEvaluationState>().CountAsync(x=>x.LogicRevision==2));Assert.Equal(0,await read.Set<AlertEvent>().CountAsync());using var detail=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var current=(await detail.Content.ReadFromJsonAsync<AlertRuleDto>())!;Assert.Equal("Unknown",current.EvaluationState);Assert.Null(current.LastSuccessAt);
    }

    [Fact] public async Task ZeroRpsIsKnownAndRecoversHighThenTriggersLowTrafficRule()
    {
        var setup=await Setup();await using var f=setup.Fixture;
        Assert.True(await Evaluate(f,Assert.Single(await Claim(f,await Slot(f),"high"))));
        using var get=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var rule=(await get.Content.ReadFromJsonAsync<AlertRuleDto>())!;
        setup.Handler.HealthOnly=true;
        using var test=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/test",rule.Definition);test.EnsureSuccessStatusCode();var result=(await test.Content.ReadFromJsonAsync<RuleTestDto>())!;Assert.Equal("Known",result.EvaluationState);Assert.False(result.Condition);Assert.Equal(0,result.Matches.Single().Values.Single().Value);
        Assert.True(await Evaluate(f,await Next(f)));
        await using(var db=f.Context()){var old=await db.Set<AlertEvent>().SingleAsync();Assert.Equal("Resolved",old.Status);Assert.Equal("Recovered",old.ResolveReason);}
        var low=rule.Definition with{Expression="request_rps < 1"};using var update=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{setup.Rule}",low,"\"1\"");update.EnsureSuccessStatusCode();
        Assert.True(await Evaluate(f,await Next(f)));await using(var db=f.Context()){Assert.Equal("Open",(await db.Set<AlertEvent>().SingleAsync(x=>x.LogicRevision==2)).Status);}
        foreach(var metric in new[]{"error_5xx_ratio","latency_p95_ms"}){using var unknown=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/test",low with{Metric=metric,Expression=metric+" < 1"});unknown.EnsureSuccessStatusCode();Assert.Equal("Unknown",(await unknown.Content.ReadFromJsonAsync<RuleTestDto>())!.EvaluationState);}
    }
    [Fact] public async Task HealthGaugeWithoutBusinessRequestsCanTriggerAndReadonlyTestIsKnown()
    {
        var setup=await Setup();await using var f=setup.Fixture;using var read=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var rule=(await read.Content.ReadFromJsonAsync<AlertRuleDto>())!;var definition=rule.Definition with{Metric="unhealthy_destinations",Expression="unhealthy_destinations > 0"};using var change=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{setup.Rule}",definition,"\"1\"");change.EnsureSuccessStatusCode();setup.Handler.HealthOnly=true;
        using var test=await f.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules/test",definition);test.EnsureSuccessStatusCode();var result=(await test.Content.ReadFromJsonAsync<RuleTestDto>())!;Assert.Equal("Known",result.EvaluationState);Assert.True(result.Condition);Assert.Equal(1,result.Matches.Single().Values.Single().Value);
        Assert.True(await Evaluate(f,Assert.Single(await Claim(f,await Slot(f),"health"))));await using var db=f.Context();Assert.Equal("Open",(await db.Set<AlertEvent>().SingleAsync()).Status);
    }

    [Fact] public async Task SourceFailureDoesNotStopStandalonePublishWorker()
    {
        await using var scenario=new DeploymentScenario();await scenario.InitializeAsync();await ObservationTestSupport.GrantAsync(scenario.Api,["alert.rule.manage"]);
        using var create=await scenario.Api.WriteAsync(HttpMethod.Post,"/api/v1/observability/alert-rules",new SaveAlertRuleRequest(scenario.Api.Organization.Id,scenario.Api.Project.Id,scenario.Api.Environment.Id,"Fault isolation","request_rps","request_rps > 1","Warning",true,0,"Environment",null,60,new(true,[])));create.EnsureSuccessStatusCode();
        var release=await scenario.ReadyAsync();using var publish=await ApiFixture.CommandAsync(scenario.Api.Client,$"/api/v1/releases/{release}/publish");publish.EnsureSuccessStatusCode();
        var handler=new AlertClockedMetricHandler();handler.Inner.Status=503;var prefix="alert-worker-test:"+Guid.NewGuid().ToString("N");
        using var worker=WorkerApp.Build(["--environment","Development"],builder=>{builder.Configuration["ConnectionStrings:WebApi"]=scenario.Api.Database.ConnectionString;builder.Configuration["Redis:Prefix"]=prefix;builder.Configuration["Observability:Enabled"]="true";builder.Configuration["Alerts:IntervalSeconds"]="1";builder.Configuration["Alerts:QueryDelaySeconds"]="0";builder.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(b=>b.PrimaryHandler=handler));builder.Logging.ClearProviders();});
        await worker.StartAsync();var progressed=false;
        try
        {
            var until=DateTimeOffset.UtcNow.AddSeconds(10);while(DateTimeOffset.UtcNow<until){await using var db=scenario.Api.Context();if(await db.Set<ReleaseRecord>().AnyAsync(x=>x.Id==release&&x.Status=="Publishing")&&await db.Set<AlertEvaluationState>().AnyAsync(x=>x.LastEvaluatedSlot!=null&&x.EvaluationState=="Unknown")){progressed=true;break;}await Task.Delay(100);}
        }
        finally
        {
            await worker.StopAsync();using var redis=await ConnectionMultiplexer.ConnectAsync("redis:6379,abortConnect=false");var key=prefix+":{"+scenario.Api.Environment.Id+"}:desired";await redis.GetDatabase().KeyDeleteAsync(key);Assert.False(await redis.GetDatabase().KeyExistsAsync(key));
        }
        Assert.True(progressed,"Alert source failure must leave queued publish processing alive.");
    }

    [Fact] public async Task RenameDuringQueryReleasesObsoleteLeaseWithoutResettingPending()
    {
        var setup=await Setup(duration:300);await using var f=setup.Fixture;Assert.True(await Evaluate(f,Assert.Single(await Claim(f,await Slot(f),"before"))));DateTimeOffset? pending;
        await using(var db=f.Context())pending=(await db.Set<AlertEvaluationState>().SingleAsync()).PendingSince;
        var next=await NextLeaseOnly(f);setup.Handler.PauseFirst=true;var running=Evaluate(f,next);await setup.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try{using var get=await f.Client.GetAsync($"/api/v1/observability/alert-rules/{setup.Rule}");var rule=(await get.Content.ReadFromJsonAsync<AlertRuleDto>())!;using var rename=await f.WriteAsync(HttpMethod.Put,$"/api/v1/observability/alert-rules/{setup.Rule}",rule.Definition with{Name="Renamed while querying"},"\"1\"").WaitAsync(TimeSpan.FromSeconds(3));rename.EnsureSuccessStatusCode();}
        finally{setup.Handler.Resume.TrySetResult(true);}
        Assert.False(await running);await using(var db=f.Context()){var state=await db.Set<AlertEvaluationState>().SingleAsync();Assert.Equal(pending,state.PendingSince);Assert.Null(state.LeaseUntil);Assert.Equal("Pending",state.Phase);}
        var current=Assert.Single(await Claim(f,await Slot(f),"renamed"));Assert.Equal(2,current.RuleRevision);Assert.Equal(1,current.LogicRevision);Assert.True(await Evaluate(f,current));
    }
    private static async Task<EvaluationLease> NextLeaseOnly(ApiFixture f){await Task.Delay(1100);return Assert.Single(await Claim(f,await Slot(f),"next"));}

}
public sealed class AlertClockedMetricHandler:HttpMessageHandler
{
    public MetricProtocolHandler Inner {get;}=new();
    public bool HealthOnly {get;set;}
    public bool PauseFirst {get;set;}
    public TaskCompletionSource<bool> Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Resume {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int paused;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        if(PauseFirst&&Interlocked.Exchange(ref paused,1)==0)return WaitAsync(request,ct);
        return Respond(request,ct);
    }
    private async Task<HttpResponseMessage> WaitAsync(HttpRequestMessage request,CancellationToken ct){Entered.TrySetResult(true);await Resume.Task.WaitAsync(ct);return await Respond(request,ct);}
    private Task<HttpResponseMessage> Respond(HttpRequestMessage request,CancellationToken ct)
    {
        var query=QueryHelpers.ParseQuery(request.RequestUri!.Query);var text=query.ContainsKey("time")?query["time"].ToString():query["end"].ToString();Inner.End=DateTimeOffset.UnixEpoch.AddSeconds(double.Parse(text,CultureInfo.InvariantCulture));
        if(HealthOnly&&query["query"].ToString().Contains("webapi_gateway_requests_total",StringComparison.Ordinal))return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=JsonContent.Create(new{status="success",data=new{resultType=request.RequestUri.AbsolutePath.EndsWith("query_range",StringComparison.Ordinal)?"matrix":"vector",result=Array.Empty<object>()}})});
        if(HealthOnly&&query["query"].ToString().Contains("webapi_destination_health",StringComparison.Ordinal))return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=JsonContent.Create(new{status="success",data=new{resultType="vector",result=new[]{new{metric=new{webapi_environment_id=Inner.EnvironmentId.ToString(),webapi_destination_id=Inner.DestinationId.ToString(),service_instance_id="node-0"},value=new object[]{Inner.End.ToUnixTimeSeconds(),"0"}}}}})});
        return new HttpMessageInvoker(Inner,false).SendAsync(request,ct);
    }
}

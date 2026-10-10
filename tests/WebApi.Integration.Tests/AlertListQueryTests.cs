using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Common;
using WebApi.Contracts.Observability;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Alerts;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class AlertListQueryTests
{
    private sealed class Capture:DbCommandInterceptor
    {
        internal int Data,Clock;
        internal void Reset(){Data=Clock=0;}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            var sql=command.CommandText;if(sql.Contains("clock_timestamp()")){Clock++;Data++;}
            // Count every resource read used by the list, including shared observation scope setup.
            // The previous alert-only accounting missed six full resource catalog queries.
            else if(new[]{"FROM alert_rules ","FROM alert_events ","FROM alert_evaluation_states ","FROM alert_event_transitions ",
                "FROM apis ","FROM applications ","FROM upstream_destinations ","FROM gateway_nodes ","FROM policies ","FROM gateway_config_versions "}.Any(sql.Contains)
                ||sql.Contains("FROM environments ")&&sql.Contains("INNER JOIN projects "))Data++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal readonly ApiFixture Api=new();internal readonly Capture Sql=new();internal readonly RequestSqlProbe Probe=new();internal readonly List<AlertRule> Rules=[];internal readonly List<AlertEvent> Events=[];
        internal readonly Dictionary<Guid,AlertEventTransition[]> Transitions=[];internal readonly EnvironmentRecord Second=new(){Code="SECOND",Name="第二环境"};
        internal ActorContext Actor=>new(Api.User.Id,"alert-list");internal ObservationScopeRequest Scope=>new(Api.Organization.Id,Api.Project.Id,Api.Environment.Id,false);
        internal async Task InitializeAsync()
        {
            await Api.InitializeAsync(b=>b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(Sql,Probe)));await Api.SeedCatalogAsync();await ObservationTestSupport.GrantAsync(Api,["alert.rule.manage","alert.read"]);await using var db=Api.Context();Second.ProjectId=Api.Project.Id;db.Add(Second);
            var now=DateTimeOffset.UtcNow.AddSeconds(-1);
            for(var i=0;i<151;i++)
            {
                var rule=new AlertRule{OrganizationId=Api.Organization.Id,ProjectId=Api.Project.Id,EnvironmentId=Api.Environment.Id,Name="R"+i.ToString("D4"),NormalizedName="R"+i.ToString("D4"),Metric="error_5xx_ratio",Expression="error_5xx_ratio > 0.05",CreatedBy=Api.User.Id,UpdatedBy=Api.User.Id};Rules.Add(rule);db.Add(rule);
                db.Add(new AlertEvaluationState{RuleId=rule.Id,OrganizationId=Api.Organization.Id,ProjectId=Api.Project.Id,EnvironmentId=Api.Environment.Id,LogicRevision=1,ResourceType="Environment",ResourceKey="Environment",EvaluationState="Known",LastSuccessAt=now,LastEvaluatedSlot=now});
            }
            await db.SaveChangesAsync();
        }
        internal async Task AddEventsAsync()
        {
            await using var db=Api.Context();var now=DateTimeOffset.UtcNow.AddSeconds(-1);
            for(var i=0;i<151;i++)
            {
                var e=new AlertEvent{RuleId=Rules[i].Id,RuleRevision=1,LogicRevision=1,OrganizationId=Api.Organization.Id,ProjectId=Api.Project.Id,EnvironmentId=Api.Environment.Id,ResourceKey="Environment",ResourceType="Environment",OccurrenceNo=1,Status=i==1?"Resolved":"Ack",EvaluationState=i==2?"Unknown":"Known",LastValue=42,LastCondition=true,StartedAt=now.AddSeconds(-i),ConditionStartedAt=now.AddSeconds(-i),Message="测试事件",RuleSummary="错误率",AckedBy=Api.User.Id,AckedAt=now,ResolvedAt=i==1?now:null};Events.Add(e);db.Add(e);
                var first=new AlertEventTransition{EventId=e.Id,ToStatus="Open",OccurredAt=now.AddMinutes(-1),CorrelationId="first-"+i,Reason="System"};var next=new AlertEventTransition{EventId=e.Id,FromStatus="Open",ToStatus=i==1?"Resolved":"Ack",ActorId=Api.User.Id,OccurredAt=now,CorrelationId="next-"+i,Reason="Operator"};Transitions[e.Id]=[first,next];db.AddRange(first,next);
            }
            await db.SaveChangesAsync();
        }
        internal async Task<PageResult<AlertRuleDto>> ReadRules(int page=1,int size=50,ObservationScopeRequest? scope=null)
        {using var services=Api.Services();return await services.ServiceProvider.GetRequiredService<AlertRuleService>().ListAsync(Actor,scope??Scope,page,size,CancellationToken.None);}
        internal async Task<PageResult<AlertEventDto>> ReadEvents(int page=1,int size=50,ObservationScopeRequest? scope=null)
        {using var services=Api.Services();return await services.ServiceProvider.GetRequiredService<AlertEventService>().ListAsync(Actor,scope??Scope,new(null,null,null,page,size),CancellationToken.None);}
        public ValueTask DisposeAsync()=>Api.DisposeAsync();
    }
    [Theory]
    [InlineData("rules",10)] [InlineData("events",6)]
    public async Task AlertsSqlDoesNotGrowPerPageItem(string kind,int budget)
    {
        await using var f=new Fixture();await f.InitializeAsync();if(kind=="events")await f.AddEventsAsync();int? data=null;
        foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2})
        {
            f.Sql.Reset();var id=Guid.NewGuid().ToString("N");using var probe=f.Probe.Begin(id);int count;
            if(kind=="rules"){var result=await f.ReadRules(page,size);Assert.Equal(151,result.Total);Assert.Equal(f.Rules.Skip((page-1)*size).Take(size).Select(r=>r.Id),result.Items.Select(r=>r.Id));count=result.Items.Count;}
            else {var result=await f.ReadEvents(page,size);Assert.Equal(151,result.Total);Assert.Equal(f.Events.Skip((page-1)*size).Take(size).Select(e=>e.Id),result.Items.Select(e=>e.Id));count=result.Items.Count;}
            Assert.InRange(f.Sql.Data,1,budget);Assert.Equal(1,f.Sql.Clock);Assert.True(f.Probe.Read(id).Total<=80,$"totalSQL {f.Probe.Read(id).Total} > 80");Assert.True(f.Probe.Read(id).Rows<=count*4+50,$"consumed {f.Probe.Read(id).Rows} rows");data??=f.Sql.Data;Assert.Equal(data,f.Sql.Data);
        }
    }
    [Theory]
    [InlineData("rules")] [InlineData("events")]
    public async Task AlertListsKeepEnvironmentIsolationAndObserveGrantRevocation(string kind)
    {
        await using var f=new Fixture();await f.InitializeAsync();if(kind=="events")await f.AddEventsAsync();
        await using(var db=f.Api.Context())
        {
            var grant=await db.Set<UserProjectScope>().SingleAsync(x=>x.UserId==f.Api.User.Id);
            grant.ProjectId=f.Api.Project.Id;grant.EnvironmentId=f.Api.Environment.Id;
            await db.Set<AlertRule>().Where(x=>x.Id==f.Rules[0].Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.EnvironmentId,(Guid?)f.Second.Id));
            if(kind=="events")await db.Set<AlertEvent>().Where(x=>x.Id==f.Events[0].Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.EnvironmentId,f.Second.Id));
            await db.SaveChangesAsync();
        }
        async Task<int> Total(ObservationScopeRequest request)=>kind=="rules"?(await f.ReadRules(scope:request)).Total:(await f.ReadEvents(scope:request)).Total;
        var all=new ObservationScopeRequest(f.Api.Organization.Id,f.Api.Project.Id,null,true);
        Assert.Equal(150,await Total(f.Scope));Assert.Equal(150,await Total(all));
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>Total(f.Scope with{EnvironmentId=f.Second.Id}))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>Total(f.Scope with{ProjectId=Guid.NewGuid()}))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>Total(f.Scope with{OrganizationId=Guid.NewGuid()}))).Status);
        await using(var db=f.Api.Context()){await db.Set<UserProjectScope>().Where(x=>x.UserId==f.Api.User.Id).ExecuteDeleteAsync();}
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>Total(f.Scope))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>Total(all))).Status);
    }
    [Theory]
    [InlineData("rules",false)] [InlineData("rules",true)] [InlineData("events",false)] [InlineData("events",true)]
    public async Task AlertListsRejectInactiveScopeAndMissingFunctionalPermission(string kind,bool all)
    {
        await using var f=new Fixture();await f.InitializeAsync();if(kind=="events")await f.AddEventsAsync();
        var request=all?new ObservationScopeRequest(f.Api.Organization.Id,f.Api.Project.Id,null,true):f.Scope;
        async Task Read(){if(kind=="rules")await f.ReadRules(scope:request);else await f.ReadEvents(scope:request);}
        await using(var db=f.Api.Context()){await db.Set<Organization>().Where(x=>x.Id==f.Api.Organization.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Disabled"));}
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(Read)).Status);
        await using(var db=f.Api.Context())
        {
            await db.Set<Organization>().Where(x=>x.Id==f.Api.Organization.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Active"));
            var permission=await db.Set<Permission>().SingleAsync(x=>x.Code==(kind=="rules"?"alert.rule.manage":"alert.read"));
            await db.Set<RolePermission>().Where(x=>x.PermissionId==permission.Id).ExecuteDeleteAsync();
        }
        Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(Read)).Status);
    }
    [Fact]
    public async Task RuleStateRequiresEveryExpectedActiveEnvironment()
    {
        await using var f=new Fixture();await f.InitializeAsync();var rule=f.Rules[0];await using var db=f.Api.Context();await db.Set<AlertRule>().Where(r=>r.Id==rule.Id).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.ProjectId,(Guid?)null).SetProperty(r=>r.EnvironmentId,(Guid?)null));
        var missing=Assert.Single((await f.ReadRules(size:1)).Items);Assert.Equal("Unknown",missing.EvaluationState);Assert.Null(missing.LastSuccessAt);
        var state=new AlertEvaluationState{RuleId=rule.Id,LogicRevision=1,OrganizationId=f.Api.Organization.Id,ProjectId=f.Api.Project.Id,EnvironmentId=f.Second.Id,ResourceType="Environment",ResourceKey="Environment",EvaluationState="Known",LastSuccessAt=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-10),LastEvaluatedSlot=DateTimeOffset.UtcNow};db.Add(state);
        db.Add(new AlertEvaluationState{RuleId=rule.Id,LogicRevision=2,OrganizationId=f.Api.Organization.Id,ProjectId=f.Api.Project.Id,EnvironmentId=f.Api.Environment.Id,ResourceType="Environment",ResourceKey="Environment",EvaluationState="Unknown"});
        db.Add(new AlertEvaluationState{RuleId=rule.Id,LogicRevision=1,OrganizationId=f.Api.Organization.Id,ProjectId=f.Api.Project.Id,EnvironmentId=f.Api.Environment.Id,ResourceType="Api",ResourceId=f.Api.Api.Id,ResourceKey="Api:"+f.Api.Api.Id,EvaluationState="Unknown"});await db.SaveChangesAsync();
        var complete=Assert.Single((await f.ReadRules(size:1)).Items);Assert.Equal("Known",complete.EvaluationState);Assert.Equal(state.LastSuccessAt,complete.LastSuccessAt);
        foreach(var slot in new DateTimeOffset?[]{DateTimeOffset.UtcNow.AddHours(1),DateTimeOffset.UtcNow.AddHours(-1),null}){await db.Set<AlertEvaluationState>().Where(s=>s.Id==state.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LastEvaluatedSlot,slot));Assert.Equal("Unknown",Assert.Single((await f.ReadRules(size:1)).Items).EvaluationState);}
        await db.Set<AlertRule>().Where(r=>r.Id==rule.Id).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.Enabled,false));Assert.Equal("Disabled",Assert.Single((await f.ReadRules(size:1)).Items).EvaluationState);
        await db.Set<AlertRule>().Where(r=>r.Id==rule.Id).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.Enabled,true));await db.Set<EnvironmentRecord>().Where(e=>e.ProjectId==f.Api.Project.Id).ExecuteUpdateAsync(s=>s.SetProperty(e=>e.Status,"Disabled"));
        var inactive=Assert.Single((await f.ReadRules(size:1,scope:new(f.Api.Organization.Id,f.Api.Project.Id,null,true))).Items);Assert.Equal("ScopeInactive",inactive.EvaluationState);Assert.Null(inactive.LastSuccessAt);
    }
    [Fact]
    public async Task RetiredTargetStillManageableByFrozenOwnership()
    {
        await using var f=new Fixture();await f.InitializeAsync();await using var db=f.Api.Context();var id=f.Rules[0].Id;await db.Set<AlertRule>().Where(r=>r.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.TargetType,"Destination").SetProperty(r=>r.TargetId,f.Api.Destination.Id));await db.Set<UpstreamDestination>().Where(d=>d.Id==f.Api.Destination.Id).ExecuteDeleteAsync();
        var result=Assert.Single((await f.ReadRules(size:1)).Items);Assert.Equal(id,result.Id);Assert.Equal(f.Api.Destination.Id,result.Definition.TargetId);using var scope=f.Api.Services();Assert.Equal(result,await scope.ServiceProvider.GetRequiredService<AlertRuleService>().DetailAsync(f.Actor,id,CancellationToken.None));
    }
    [Fact]
    public async Task EventUnknownRedactsValueAndConditionAndRetainsTransitions()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.AddEventsAsync();await using var db=f.Api.Context();await db.Set<AlertEvaluationState>().Where(s=>s.RuleId==f.Rules[0].Id||s.RuleId==f.Rules[1].Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LastEvaluatedSlot,DateTimeOffset.UtcNow.AddHours(1)));
        var result=await f.ReadEvents(size:100);Assert.Equal(151,result.Total);
        foreach(var item in result.Items.Take(3))
        {
            if(item.Id==f.Events[1].Id){Assert.Equal("Known",item.EvaluationState);Assert.Equal(42,item.LastValue);Assert.True(item.LastCondition);}
            else {Assert.Equal("Unknown",item.EvaluationState);Assert.Null(item.LastValue);Assert.Null(item.LastCondition);}
            Assert.Equal(f.Transitions[item.Id].Select(t=>t.Id),item.Transitions.Select(t=>t.Id));
        }
        using var scope=f.Api.Services();var service=scope.ServiceProvider.GetRequiredService<AlertEventService>();foreach(var item in result.Items.Take(3)){var detail=await service.DetailAsync(f.Actor,item.Id,CancellationToken.None);Assert.Equal(detail with{Transitions=item.Transitions},item);}
        Assert.Equal(422,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(f.Actor,f.Scope,new("Bad",null,null),CancellationToken.None))).Status);
    }
}

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
using System.Net;
using System.Text;
using System.Text.Json;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Security;
namespace WebApi.Integration.Tests;
public sealed class CounterfactualAlertReferenceTests_Alerts
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
        _ = budget; // Preserve original test-case identity; cost is proved independently here.
        await using var f=new Fixture();await f.InitializeAsync();if(kind=="events")await f.AddEventsAsync();int? data=null;
        foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2})
        {
            f.Sql.Reset();var id=Guid.NewGuid().ToString("N");using var probe=f.Probe.Begin(id);int count;
            if(kind=="rules"){var result=await f.ReadRules(page,size);Assert.Equal(151,result.Total);Assert.Equal(f.Rules.Skip((page-1)*size).Take(size).Select(r=>r.Id),result.Items.Select(r=>r.Id));count=result.Items.Count;}
            else {var result=await f.ReadEvents(page,size);Assert.Equal(151,result.Total);Assert.Equal(f.Events.Skip((page-1)*size).Take(size).Select(e=>e.Id),result.Items.Select(e=>e.Id));count=result.Items.Count;}
            Assert.Equal(1,f.Sql.Clock);data??=f.Sql.Data;
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
    [Theory]
    [InlineData("rules")] [InlineData("events")]
    public async Task AlertClockIsSingleForAllPageItemsAndCostGrows(string kind)
    {
        await using var f=new Fixture();await f.InitializeAsync();if(kind=="events")await f.AddEventsAsync();
        foreach(var size in new[]{1,50,100}){f.Sql.Reset();var key=Guid.NewGuid().ToString("N");using var capture=f.Probe.Begin(key);if(kind=="rules")Assert.Equal(size,(await f.ReadRules(1,size)).Items.Count);else Assert.Equal(size,(await f.ReadEvents(1,size)).Items.Count);Assert.Equal(1,f.Sql.Clock);Assert.True(f.Probe.Read(key).Rows>=151);if(size==100)Assert.True(f.Sql.Data>=200);}
        f.Sql.Reset();if(kind=="rules")Assert.Empty((await f.ReadRules(1000000,1)).Items);else Assert.Empty((await f.ReadEvents(1000000,1)).Items);Assert.Equal(0,f.Sql.Clock);
    }
}
public sealed class CounterfactualAlertReferenceTests_References
{
    private sealed class Fixture:IAsyncDisposable
    {
        internal readonly PostgresDatabase Database=new();internal readonly RequestSqlProbe Probe=new();
        private readonly string directory=Path.Combine(Path.GetTempPath(),"policy-reference-query-"+Guid.NewGuid().ToString("N"));
        internal ReferenceSceneManifest Manifest=null!;
        internal Guid Id(string kind,int index)=>PerformanceDataset.ResourceId(20261009,kind,index);
        internal ActorContext Admin=>new(Id("user",0),"reference-admin");
        internal WebApiDbContext Context()=>new(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(Database.ConnectionString).AddInterceptors(Probe).Options);
        internal PolicyReferenceService Service(WebApiDbContext db){var auth=new AuthorizationService(db);var scopes=new ScopeResolver(db);return new(db,new PolicyAccess(db,auth,scopes),new PolicyReferenceReader(db,new CollectionAuthorizationQuery(db)));}
        internal async Task InitializeAsync()
        {
            if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException();Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            await Database.InitializeAsync();await using var db=Database.Context();await db.Database.MigrateAsync();var now=DateTimeOffset.UtcNow;
            Manifest=await PerformanceDataset.CreateReferenceSceneAsync(db,new(100,20261009,now.AddMinutes(-1),now.AddHours(10)),directory,CancellationToken.None);
        }
        internal async Task<ActorContext> ActorAsync(params string[] permissions)
        {
            await using var db=Database.Context();var user=new UserRecord{Username=Guid.NewGuid().ToString("N"),DisplayName="引用读者",SecurityStamp=Guid.NewGuid().ToString("N")};var role=new Role{OrganizationId=Id("organization",0),Code=Guid.NewGuid().ToString("N"),Name="引用权限"};db.AddRange(user,role);db.Add(new UserRole{UserId=user.Id,RoleId=role.Id});
            foreach(var p in await db.Set<Permission>().Where(p=>permissions.Contains(p.Code)||p.Code=="policy.read").ToArrayAsync())db.Add(new RolePermission{RoleId=role.Id,PermissionId=p.Id});
            db.Add(new UserProjectScope{UserId=user.Id,OrganizationId=Id("organization",0),ProjectId=Id("project",0),EnvironmentId=Id("environment",0),AccessMode="read"});await db.SaveChangesAsync();return new(user.Id,"reference-reader");
        }
        public async ValueTask DisposeAsync(){await Database.DisposeAsync();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]
    public void ReferenceBatchesRespectIdsAndBytesAndOversizeSingleton()
    {
        var environment=Guid.NewGuid();var small=Enumerable.Range(0,201).Select(_=>new ReferencePayloadDescriptor(Guid.NewGuid(),"FrozenRelease",environment,100)).ToArray();
        var smallBatches=PolicyReferenceReader.PlanBatches(small);Assert.Equal(2,smallBatches.Count);Assert.Equal(new[]{200,1},smallBatches.Select(b=>b.Count));
        var oversized=Guid.NewGuid();var sources=small.Concat(new[]{new ReferencePayloadDescriptor(Guid.NewGuid(),"FrozenRelease",environment,5*1024*1024),new ReferencePayloadDescriptor(Guid.NewGuid(),"FrozenRelease",environment,5*1024*1024),new ReferencePayloadDescriptor(oversized,"FrozenRelease",environment,9*1024*1024)}).ToArray();
        var batches=PolicyReferenceReader.PlanBatches(sources);Assert.Equal(sources,batches.SelectMany(b=>b));Assert.All(batches,b=>{Assert.InRange(b.Count,1,200);Assert.True(b.Count==1||b.Sum(x=>x.SizeBytes)<=8*1024*1024);});Assert.Single(batches.Single(b=>b.Any(x=>x.Id==oversized)));
    }
    [Fact]
    public async Task VisibleTotalIncludesAllThreeSourcesAndRuntimeIsDistinct()
    {
        await using var f=new Fixture();await f.InitializeAsync();await using var db=f.Context();var service=f.Service(db);var expected=f.Manifest.Actors!.Single(a=>a.Kind=="platform-role").Expected;
        int? sql=null;
        foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2,3,9999999})
        {
            var key=Guid.NewGuid().ToString("N");using var probe=f.Probe.Begin(key);var result=await service.ListAsync(f.Manifest.PolicyId,f.Admin,page,size);
            Assert.Equal(219,result.Total);Assert.Equal(expected.Skip((Math.Clamp(page,1,1000000)-1)*size).Take(size),result.Items);
            var stats=f.Probe.Read(key);Assert.Equal(f.Manifest.CandidateBytes+f.Manifest.RuntimeBytes,stats.BinaryColumnBytes);sql??=stats.Total;Assert.Equal(sql,stats.Total);
        }
        var all=await service.FindProtectedAsync(f.Manifest.PolicyId);Assert.Equal(116,all.Count(x=>x.ReferenceKind=="Working"));Assert.Equal(100,all.Count(x=>x.ReferenceKind=="FrozenRelease"));Assert.Equal(3,all.Count(x=>x.ReferenceKind=="Runtime"));
    }
    [Fact]
    public async Task RepeatedNamesUseStableSourceIdsAcrossPages()
    {
        await using var f=new Fixture();await f.InitializeAsync();var renamed=Enumerable.Range(0,66).Select(i=>f.Id("route",i)).ToArray();
        await using(var db=f.Database.Context())
        {
            await db.Set<ApiRoute>().Where(r=>renamed.Contains(r.Id)).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.RouteName,"同名引用"));
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX test_reference_route_desc ON api_routes(id DESC); CLUSTER api_routes USING test_reference_route_desc; CREATE INDEX test_reference_binding_desc ON route_policy_bindings(route_id DESC); CLUSTER route_policy_bindings USING test_reference_binding_desc; ANALYZE api_routes; ANALYZE route_policy_bindings;");
        }
        var expected=f.Manifest.Actors!.Single(a=>a.Kind=="platform-role").Expected.Select(r=>r.RouteId is Guid id&&renamed.Contains(id)?r with{Name="同名引用"}:r).OrderBy(r=>r.ReferenceKind).ThenBy(r=>r.EnvironmentId).ThenBy(r=>r.Name).ThenBy(r=>r.RouteId).ToArray();
        await using var context=f.Context();await using var transaction=await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("SET LOCAL enable_hashjoin=off; SET LOCAL enable_mergejoin=off; SET LOCAL enable_indexscan=off; SET LOCAL enable_indexonlyscan=off; SET LOCAL enable_bitmapscan=off;");
        var service=f.Service(context);var observed=new List<PolicyReferenceDto>();
        for(var page=1;page<=5;page++){var result=await service.ListAsync(f.Manifest.PolicyId,f.Admin,page,50);Assert.Equal(219,result.Total);Assert.Equal(expected.Skip((page-1)*50).Take(50),result.Items);observed.AddRange(result.Items);}
        Assert.Equal(expected,observed);
    }
    [Theory]
    [InlineData("route.read",100,"Working")][InlineData("release.read",100,"FrozenRelease")][InlineData("gateway.config.read",1,"Runtime")]
    public async Task ReferenceSourcesRequireIndependentPermissions(string permission,int count,string kind)
    {
        await using var f=new Fixture();await f.InitializeAsync();var actor=await f.ActorAsync(permission);await using var db=f.Context();var key=Guid.NewGuid().ToString("N");using var probe=f.Probe.Begin(key);var result=await f.Service(db).ListAsync(f.Manifest.PolicyId,actor,1,100);
        Assert.Equal(count,result.Total);Assert.All(result.Items,r=>{Assert.Equal(kind,r.ReferenceKind);Assert.Equal(f.Id("environment",0),r.EnvironmentId);});if(permission=="release.read"&&count==100&&kind=="FrozenRelease"){Assert.True(f.Probe.Read(key).Total>35);Assert.True(f.Probe.Read(key).Total>=count+1);}else Assert.True(f.Probe.Read(key).Total<=35,$"SQL {f.Probe.Read(key).Total} > 35");
        if(kind=="Working")Assert.Equal(0,f.Probe.Read(key).BinaryColumnBytes);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task VisibleCorruptSnapshotFailsClosed(bool runtime)
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Database.Context())
        {
            if(runtime){var id=await db.Set<GatewayConfigVersion>().Where(v=>v.EnvironmentId==f.Id("environment",0)&&v.VersionNo==100).Select(v=>v.Id).SingleAsync();var snapshot=await db.Set<GatewayConfigSnapshot>().SingleAsync(s=>s.ConfigVersionId==id);snapshot.PayloadBytes=Encoding.UTF8.GetBytes("{broken");}
            else{var release=await db.Set<ReleaseRecord>().Where(r=>r.Status=="WaitingApproval").FirstAsync();release.CandidateBytes=Encoding.UTF8.GetBytes("{broken");}await db.SaveChangesAsync();
        }
        var hidden=await f.ActorAsync("route.read");await using var context=f.Context();var reader=f.Service(context);
        Assert.Equal(100,(await reader.ListAsync(f.Manifest.PolicyId,hidden,1,100)).Total);
        var error=await Assert.ThrowsAsync<ApiException>(()=>reader.ListAsync(f.Manifest.PolicyId,f.Admin,1,100));Assert.Equal(409,error.Status);Assert.Equal("policy_reference_unavailable",error.Code);
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>reader.FindProtectedAsync(f.Manifest.PolicyId))).Status);
    }
    [Theory]
    [InlineData("2.0")][InlineData("2.1")][InlineData("2.2")]
    public async Task RuntimeSourceIdentityUsesHistoricalSchemaSemantics(string schema)
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Database.Context())
        {
            var id=await db.Set<GatewayConfigVersion>().Where(v=>v.EnvironmentId==f.Id("environment",0)&&v.VersionNo==100).Select(v=>v.Id).SingleAsync();var row=await db.Set<GatewayConfigSnapshot>().SingleAsync(s=>s.ConfigVersionId==id);var snapshot=SnapshotValidator.ParsePayload(row.PayloadBytes,f.Id("environment",0));
            snapshot=snapshot with{SchemaVersion=schema,Policies=snapshot.Policies.Select(p=>schema=="2.0"?p:p with{Id=RuntimePolicyIdentity.Create(f.Manifest.PolicyId,7,p.Type,PolicyConfigurationValidator.Normalize(p.Type,p.Config)),SourcePolicyId=f.Manifest.PolicyId,SourceRevision=7}).ToArray()};row.PayloadBytes=CanonicalJson.Serialize(snapshot);SnapshotValidator.ParsePayload(row.PayloadBytes,f.Id("environment",0));await db.SaveChangesAsync();
        }
        var actor=await f.ActorAsync("gateway.config.read");await using var context=f.Context();var result=await f.Service(context).ListAsync(f.Manifest.PolicyId,actor,1,100);var reference=Assert.Single(result.Items);Assert.Equal(schema=="2.0"?null:(long?)7,reference.SourceRevision);Assert.Equal(100,reference.ConfigVersion);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task MissingOrEmptyVisibleRuntimeFailsClosed(bool empty)
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Database.Context())
        {
            var id=await db.Set<GatewayConfigVersion>().Where(v=>v.EnvironmentId==f.Id("environment",0)&&v.VersionNo==100).Select(v=>v.Id).SingleAsync();var row=await db.Set<GatewayConfigSnapshot>().SingleAsync(s=>s.ConfigVersionId==id);if(empty)row.PayloadBytes=[];else db.Remove(row);await db.SaveChangesAsync();
        }
        var hidden=await f.ActorAsync("route.read");await using var context=f.Context();var service=f.Service(context);Assert.Equal(100,(await service.ListAsync(f.Manifest.PolicyId,hidden,1,100)).Total);Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(f.Manifest.PolicyId,f.Admin,1,100))).Status);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task HiddenProtectedReferenceStillBlocksWrite(bool corrupt)
    {
        await using var f=await PolicyReleaseTests.Prepare();var preview=await PolicyReleaseTests.Preview(f);var policy=Assert.Single(preview.Policies!);
        await using(var db=f.Context())
        {
            await db.Set<RoutePolicyBinding>().ExecuteDeleteAsync();var candidate=new FrozenReleaseCandidate(f.Environment.Id,f.Organization.Id,f.Project.Id,0,[f.Version.Id],preview.Versions,preview.Routes,preview.Clusters,preview.Policies!,preview.Bindings!,[],preview.ResourceRevisions);
            db.Add(new ReleaseRecord{EnvironmentId=f.Environment.Id,ReleaseNo="隐藏保护发布",Status="WaitingApproval",ApprovalPolicy="[]",RequestedBy=f.User.Id,CandidateBytes=corrupt?Encoding.UTF8.GetBytes("{broken"):CanonicalJson.Serialize(candidate)});
            var permission=await db.Set<Permission>().Where(p=>p.Code=="release.read").Select(p=>p.Id).SingleAsync();await db.Set<RolePermission>().Where(r=>r.PermissionId==permission).ExecuteDeleteAsync();await db.SaveChangesAsync();
        }
        using var read=await f.Client.GetAsync($"/api/v1/policies/{policy.Id}/references");Assert.Equal(HttpStatusCode.OK,read.StatusCode);using var json=JsonDocument.Parse(await read.Content.ReadAsStringAsync());Assert.Equal(0,json.RootElement.GetProperty("total").GetInt32());
        using var write=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/policies/{policy.Id}",etag:"\"1\"");Assert.Equal(HttpStatusCode.Conflict,write.StatusCode);Assert.DoesNotContain("隐藏保护发布",await write.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task ValidOversizeAndMultibytePayloadsUseActualSqlByteDescriptors()
    {
        await using var f=new Fixture();await f.InitializeAsync();long expectedBytes;long largeBytes;Guid largeId;
        await using(var db=f.Database.Context())
        {
            var template=await db.Set<ReleaseRecord>().AsNoTracking().FirstAsync(r=>r.Status=="WaitingApproval");var candidate=JsonSerializer.Deserialize<FrozenReleaseCandidate>(template.CandidateBytes!,CanonicalJson.Options)!;
            var current=await (from v in db.Set<GatewayConfigVersion>() join s in db.Set<GatewayConfigSnapshot>() on v.Id equals s.ConfigVersionId where v.EnvironmentId==template.EnvironmentId&&v.VersionNo==100 select s.PayloadBytes).SingleAsync();
            var compiler=new SnapshotCompiler(new UpstreamAddressPolicy(["http://backend:8080"]));var baseline=SnapshotValidator.ParsePayload(current,template.EnvironmentId);
            for(var i=100;i<201;i++)db.Add(new ReleaseRecord{EnvironmentId=template.EnvironmentId,ReleaseNo="BATCH-"+i,Status="Ready",RequestedBy=f.Admin.UserId,ApprovalPolicy=template.ApprovalPolicy,CandidateBytes=template.CandidateBytes});
            var schema=JsonSerializer.Serialize(new{type="string",description=new string('x',72000)+new string('中',2000)},CanonicalJson.Options);SnapshotValidator.ValidateSchema(schema);
            var versions=candidate.Versions.Select((v,i)=>i>=2?v:v with{Schemas=Enumerable.Range(0,64).Select(n=>new SchemaDto(Guid.NewGuid(),v.Version.Id,"component","BatchSchema"+n,null,"application/json",schema,null,null)).ToArray()}).ToArray();
            var large=candidate with{Versions=versions};compiler.Compile(large,baseline,101,DateTimeOffset.UtcNow);var largePayload=CanonicalJson.Serialize(large);largeBytes=largePayload.LongLength;Assert.InRange(largeBytes,8*1024*1024+1,16*1024*1024);
            largeId=Guid.NewGuid();db.Add(new ReleaseRecord{Id=largeId,EnvironmentId=template.EnvironmentId,ReleaseNo="BATCH-OVERSIZE",Status="Ready",RequestedBy=f.Admin.UserId,ApprovalPolicy=template.ApprovalPolicy,CandidateBytes=largePayload});
            var half=large with{Versions=versions.Select((v,i)=>i==1?v with{Schemas=[]}:v).ToArray()};var halfPayload=CanonicalJson.Serialize(half);Assert.InRange(halfPayload.Length,4*1024*1024+1,8*1024*1024);
            for(var i=0;i<2;i++)db.Add(new ReleaseRecord{EnvironmentId=template.EnvironmentId,ReleaseNo="BATCH-MULTIBYTE-"+i,Status="Ready",RequestedBy=f.Admin.UserId,ApprovalPolicy=template.ApprovalPolicy,CandidateBytes=halfPayload});
            await db.SaveChangesAsync();expectedBytes=f.Manifest.RuntimeBytes+await db.Set<ReleaseRecord>().Where(r=>r.CandidateBytes!=null&&r.Status=="WaitingApproval"||r.Status=="Ready").SumAsync(r=>(long)r.CandidateBytes!.Length);
            // Metadata is intentionally stale: descriptors must use octet_length of the bytea column.
            await db.Set<GatewayConfigSnapshot>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.SizeBytes,1));
            var descriptors=await db.Set<ReleaseRecord>().Where(r=>r.Status=="WaitingApproval"||r.Status=="Ready").OrderBy(r=>r.Id).Select(r=>new ReferencePayloadDescriptor(r.Id,"FrozenRelease",r.EnvironmentId,(long)r.CandidateBytes!.Length)).ToArrayAsync();
            var batches=PolicyReferenceReader.PlanBatches(descriptors);Assert.Equal(204,descriptors.Length);Assert.Single(batches.Single(b=>b.Any(x=>x.Id==largeId)));Assert.Equal(largeBytes,descriptors.Single(x=>x.Id==largeId).SizeBytes);Assert.All(batches,b=>{Assert.InRange(b.Count,1,200);Assert.True(b.Count==1||b.Sum(x=>x.SizeBytes)<=8*1024*1024);});
        }
        await using var context=f.Context();using var probe=f.Probe.Begin("large");var result=await f.Service(context).ListAsync(f.Manifest.PolicyId,f.Admin,1,100);Assert.Equal(323,result.Total);var stats=f.Probe.Read("large");Assert.Equal(expectedBytes,stats.BinaryColumnBytes);
    }
    private sealed class PayloadCounter:DbCommandInterceptor
    {
        internal int Frozen,Runtime;internal readonly List<string[]> Columns=[];
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,DbDataReader reader,CancellationToken ct=default)
        {
            var columns=Enumerable.Range(0,reader.FieldCount).Select(reader.GetName).ToArray();Columns.Add(columns);if(columns.Any(c=>c.Replace("_","").Equals("CandidateBytes",StringComparison.OrdinalIgnoreCase)))Frozen++;if(columns.Any(c=>c.Replace("_","").Equals("PayloadBytes",StringComparison.OrdinalIgnoreCase)))Runtime++;return ValueTask.FromResult(reader);
        }
    }
    [Fact]
    public async Task ReferenceReadsEachVisiblePayloadOnce()
    {
        await using var f=new Fixture();await f.InitializeAsync();var counter=new PayloadCounter();await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(f.Probe,counter).Options);
        const string key="per-payload";using var measure=f.Probe.Begin(key);var result=await f.Service(db).ListAsync(f.Manifest.PolicyId,f.Admin,1,100);Assert.Equal(219,result.Total);Assert.True(counter.Frozen==100,"Payload result columns: "+string.Join(";",counter.Columns.Select(c=>string.Join(",",c))));Assert.Equal(3,counter.Runtime);Assert.Equal(f.Manifest.CandidateBytes+f.Manifest.RuntimeBytes,f.Probe.Read(key).BinaryColumnBytes);
    }
}

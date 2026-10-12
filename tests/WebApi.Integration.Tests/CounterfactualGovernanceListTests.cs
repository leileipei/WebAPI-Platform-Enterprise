using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Governance;
using System.Net.Http.Json;
using WebApi.Contracts.Security;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;
using Xunit.Abstractions;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.Releases;
using WebApi.ControlPlane;
using WebApi.Infrastructure.Releases;
namespace WebApi.Integration.Tests;

public sealed class CounterfactualGovernanceListTests(ITestOutputHelper output)
{
    private sealed class Capture : DbCommandInterceptor
    {
        internal string Kind="";internal int Data;internal int Associations;internal readonly List<string> Templates=[];
        internal void Reset(string kind){Kind=kind;Data=Associations=0;Templates.Clear();}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            var sql=command.CommandText;Templates.Add(sql);
            var relation=Kind switch {"users"=>new[]{"FROM users ","FROM user_roles "},"nodes"=>new[]{"FROM gateway_nodes "},"events"=>new[]{"FROM gateway_node_events "},_=>new[]{"FROM gateway_config_versions ","FROM gateway_config_snapshots ","FROM release_records "}};
            // Scope identifiers alone are authorization; full DTO/environment state is resource data.
            // Include prerequisites used by gateway lists instead of hiding them outside the page budget.
            var metadata=Kind!="users"&&sql.Contains("desired_config_version")
                ||Kind=="events"&&sql.Contains("FROM gateway_nodes ")&&sql.Contains("current_config_version");
            if(!sql.StartsWith("SELECT EXISTS",StringComparison.Ordinal)&&!sql.Contains("INNER JOIN roles ")&&(relation.Any(sql.Contains)||metadata))Data++;
            if(Kind=="users"&&sql.Contains("FROM user_roles ")&&!sql.Contains("INNER JOIN roles "))Associations++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ApiFixture Api=new();internal readonly Capture Sql=new();internal readonly RequestSqlProbe Probe=new();internal readonly List<UserRecord> Users=[];internal readonly List<GatewayNode> Nodes=[];internal readonly Dictionary<Guid,Guid[]> Roles=[];internal long LastRows;internal readonly Dictionary<long,ReleaseRecord> Latest=[];
        internal ActorContext Actor=>new(Api.User.Id,"governance-query");
        internal async Task InitializeAsync()
        {
            await Api.InitializeAsync(b=>b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(Sql,Probe)));await Api.SeedScopeAsync(platformAdmin:true);await using var db=Api.Context();Users.Add(Api.User);
            var admin=await db.Set<UserRole>().Where(r=>r.UserId==Api.User.Id).Select(r=>r.RoleId).SingleAsync();Roles[Api.User.Id]=[admin];
            foreach(var code in new[]{"gateway.read","gateway.config.read","gateway.operate"}){var p=new Permission{Code=code,Module="gateway",Name=code};db.Add(p);db.Add(new RolePermission{RoleId=admin,PermissionId=p.Id});}
            var second=new Role{Code="QUERY-SECOND",Name="第二角色"};db.Add(second);
            for(var i=0;i<150;i++){var u=new UserRecord{Username="U"+i.ToString("D4"),DisplayName="重复显示名称",SecurityStamp=Guid.NewGuid().ToString("N")};Users.Add(u);Roles[u.Id]=[admin,second.Id];db.Add(u);db.AddRange(new UserRole{UserId=u.Id,RoleId=admin},new UserRole{UserId=u.Id,RoleId=second.Id});}
            for(var i=0;i<151;i++){var n=new GatewayNode{EnvironmentId=Api.Environment.Id,NodeName="N"+i.ToString("D4"),InstanceId="instance-"+i,AppVersion="test",CurrentConfigVersion=3,CurrentDeploymentSequence=9,TargetConfigVersion=7,LastHeartbeatAt=i%2==0?DateTimeOffset.UtcNow:DateTimeOffset.UtcNow.AddMinutes(-10)};Nodes.Add(n);db.Add(n);}
            var env=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==Api.Environment.Id);env.DesiredConfigVersion=7;env.DeploymentSequence=23;await db.SaveChangesAsync();
        }
        internal async Task<T> Measure<T>(string kind,int budget,int rows,Func<IServiceProvider,Task<T>> action)
        {
            Sql.Reset(kind);var id=Guid.NewGuid().ToString("N");using var probe=Probe.Begin(id);using var scope=Api.Services();var result=await action(scope.ServiceProvider);var stats=Probe.Read(id);LastRows=stats.Rows;
            return result;
        }
        internal async Task AddSnapshotsAsync(bool credential=false)
        {
            await using var db=Api.Context();var material=ApiKeySecret.Create();
            for(var i=1;i<=151;i++)
            {
                var apps=credential?new[]{new RuntimeApplication(Guid.NewGuid(),"Active",[new RuntimeCredential(Guid.NewGuid(),material.AccessKey,material.Hash,"Active",DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1))],[])}:Array.Empty<RuntimeApplication>();
                var payload=JsonSerializer.SerializeToUtf8Bytes(new RuntimeSnapshot("2.0",Api.Environment.Id,i,DateTimeOffset.UtcNow,[],[],[],apps),CanonicalJson.Options);SnapshotValidator.ParsePayload(payload,Api.Environment.Id);
                var v=new GatewayConfigVersion{EnvironmentId=Api.Environment.Id,VersionNo=i,CreatedBy=Api.User.Id,SnapshotHash=Convert.ToHexStringLower(SHA256.HashData(payload))};db.Add(v);db.Add(new GatewayConfigSnapshot{ConfigVersionId=v.Id,Payload="{}",PayloadBytes=payload,SizeBytes=payload.LongLength});
                var oldest=new ReleaseRecord{EnvironmentId=Api.Environment.Id,ReleaseNo="old-"+i,RequestedBy=Api.User.Id,ToConfigVersion=i,ReleaseType="publish",Status="Succeeded",DeploymentSequence=i,CreatedAt=DateTimeOffset.UtcNow};
                var latest=new ReleaseRecord{EnvironmentId=Api.Environment.Id,ReleaseNo="latest-"+i,RequestedBy=Api.User.Id,ToConfigVersion=i,ReleaseType="rollback",Status="RolledBack",DeploymentSequence=i+1000,CreatedAt=DateTimeOffset.UtcNow.AddDays(-1)};
                db.AddRange(oldest,latest,new ReleaseRecord{EnvironmentId=Api.Environment.Id,ReleaseNo="unpublished-"+i,RequestedBy=Api.User.Id,ToConfigVersion=i,ReleaseType="publish",Status="Ready",DeploymentSequence=0,CreatedAt=DateTimeOffset.UtcNow.AddHours(1)});Latest[i]=latest;
            }
            await db.SaveChangesAsync();
        }
        public ValueTask DisposeAsync()=>Api.DisposeAsync();
    }
    [Fact]
    public async Task UserRolesLoadedOnlyForPage()
    {
        await using var f=new Fixture();await f.InitializeAsync();var ids=f.Users.OrderBy(u=>u.Username,StringComparer.Ordinal).ThenBy(u=>u.Id.ToString("D"),StringComparer.Ordinal).Select(u=>u.Id).ToArray();
        foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2,4,1000001})
        {
            var expected=ids.Skip((Math.Min(page,1000000)-1)*size).Take(size).ToArray();var result=await f.Measure("users",3,expected.Length*3+12,p=>p.GetRequiredService<GovernanceService>().UsersAsync(f.Actor,page,size));
            Assert.Equal(151,result.Total);Assert.Equal(expected,result.Items.Select(u=>u.Id));Assert.Equal(Math.Min(page,1000000),result.Page);await using(var oracleDb=f.Api.Context()){var rows=await oracleDb.Set<UserRole>().AsNoTracking().Where(r=>expected.Contains(r.UserId)).Select(r=>new{r.UserId,r.RoleId}).ToArrayAsync();var roles=rows.ToLookup(r=>r.UserId,r=>r.RoleId);Assert.All(result.Items,u=>{var original=f.Users.Single(x=>x.Id==u.Id);var oracle=new UserDto(original.Id,original.Username,original.DisplayName,original.Email,original.Status,original.AuthSource,original.Revision,roles[u.Id].ToArray());Assert.Equal(CanonicalJson.Serialize(oracle),CanonicalJson.Serialize(u));});}Assert.Equal(expected.Length==0?0:1,f.Sql.Associations);
        }
    }
    [Fact]
    public async Task GatewayNodesRetainEnvironmentStateAndEtag()
    {
        await using var f=new Fixture();await f.InitializeAsync();
        foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2,4,1000001})
        {
            var expected=f.Nodes.Skip((Math.Min(page,1000000)-1)*size).Take(size).ToArray();var result=await f.Measure("nodes",2,expected.Length+12,p=>p.GetRequiredService<GatewayReadService>().NodesAsync(f.Api.Environment.Id,f.Actor,page,size));
            Assert.Equal(151,result.Total);Assert.Equal(expected.Select(n=>n.Id),result.Items.Select(n=>n.Id));Assert.All(result.Items,n=>{Assert.Equal(7,n.DesiredConfigVersion);Assert.Equal(23,n.DesiredDeploymentSequence);Assert.Equal(3,n.CurrentConfigVersion);Assert.Equal(9,n.CurrentDeploymentSequence);Assert.Equal($"\"{n.InstanceId}:True\"",n.ETag);Assert.Equal(n.LastHeartbeatAt<DateTimeOffset.UtcNow.AddSeconds(-120),n.Offline);});
        }
        using var scope=f.Api.Services();var service=scope.ServiceProvider.GetRequiredService<GatewayReadService>();var first=await service.NodeAsync(f.Nodes[0].Id,f.Actor);var disabled=await service.SetEnabledAsync(first.Id,false,first.ETag,f.Actor);Assert.False(disabled.Enabled);Assert.Equal($"\"{first.InstanceId}:False\"",disabled.ETag);
        Assert.Equal(412,(await Assert.ThrowsAsync<ApiException>(()=>service.SetEnabledAsync(first.Id,true,first.ETag,f.Actor))).Status);
        await using var db=f.Api.Context();await db.Set<UserProjectScope>().Where(s=>s.UserId==f.Actor.UserId).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.AccessMode,"read"));
        using var nextRequest=f.Api.Services();Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(()=>nextRequest.ServiceProvider.GetRequiredService<GatewayReadService>().SetEnabledAsync(first.Id,true,disabled.ETag,f.Actor))).Status);
    }
    [Fact]
    public async Task EventsCapTotalAt10000AndPageWithinCap()
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Api.Context()){db.AddRange(Enumerable.Range(1,10003).Select(i=>new GatewayNodeEvent{Id=i,GatewayNodeId=f.Nodes[0].Id,EventType="Query",Message="event "+i}));await db.SaveChangesAsync();}
        var last=await f.Measure("events",2,112,p=>p.GetRequiredService<GatewayReadService>().EventsAsync(f.Nodes[0].Id,f.Actor,100,100));Assert.True(f.LastRows>=10000);Assert.Equal(10000,last.Total);Assert.Equal(Enumerable.Range(4,100).Reverse().Select(i=>(long)i),last.Items.Select(e=>e.Id));
        var first=await f.Measure("events",2,13,p=>p.GetRequiredService<GatewayReadService>().EventsAsync(f.Nodes[0].Id,f.Actor,1,1));Assert.Equal(10003,Assert.Single(first.Items).Id);
        var empty=await f.Measure("events",2,12,p=>p.GetRequiredService<GatewayReadService>().EventsAsync(f.Nodes[0].Id,f.Actor,1000001,100));Assert.Empty(empty.Items);Assert.Equal(10000,empty.Total);Assert.Equal(1000000,empty.Page);
        await using(var db=f.Api.Context()){db.AddRange(Enumerable.Range(20000,10003).Select(i=>new GatewayNodeEvent{Id=i,GatewayNodeId=f.Nodes[1].Id,EventType="Other",Message="unrelated"}));await db.SaveChangesAsync();}
        var unchanged=await f.Measure("events",2,112,p=>p.GetRequiredService<GatewayReadService>().EventsAsync(f.Nodes[0].Id,f.Actor,100,100));Assert.Equal(last.Total,unchanged.Total);Assert.Equal(last.Items,unchanged.Items);
    }
    [Fact]
    public async Task EventOutOfRangeKeepsCappedTotalWithoutReadingEvents()
    {
        await using var f=new Fixture();await f.InitializeAsync();
        await using(var db=f.Api.Context()){db.AddRange(Enumerable.Range(1,10003).Select(i=>new GatewayNodeEvent{Id=i,GatewayNodeId=f.Nodes[0].Id,EventType="Query",Message="event "+i}));await db.SaveChangesAsync();}
        f.Sql.Reset("events");using var probe=f.Probe.Begin("capped-empty");using var scope=f.Api.Services();
        var result=await scope.ServiceProvider.GetRequiredService<GatewayReadService>().EventsAsync(f.Nodes[0].Id,f.Actor,10001,1);
        Assert.Equal(10000,result.Total);Assert.Empty(result.Items);Assert.True(f.Probe.Read("capped-empty").Rows>=10000);
        // Diagnostic output contains command templates only, never parameter values.
        // It lets source review bind the provider's capped-empty shape to actual PostgreSQL execution.
        foreach(var sql in f.Sql.Templates.Where(s=>s.Contains("FROM gateway_node_events ")))
            output.WriteLine("SQL_TEMPLATE_SOURCE_REVIEW "+JsonSerializer.Serialize(new {immutableSource=false,performanceAcceptance=false,
                hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))),sql}));
    }
    [Fact]
    public async Task SnapshotListDoesNotLoadPayloadAndUsesLatestSequence()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.AddSnapshotsAsync();
        foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2,4,1000001})
        {
            var expected=Enumerable.Range(1,151).Reverse().Skip((Math.Min(page,1000000)-1)*size).Take(size).ToArray();var result=await f.Measure("snapshots",3,expected.Length*2+12,p=>p.GetRequiredService<GatewayReadService>().SnapshotsAsync(f.Api.Environment.Id,f.Actor,page,size));
            Assert.Equal(151,result.Total);Assert.Equal(expected.Select(i=>(long)i),result.Items.Select(s=>s.ConfigVersion));Assert.All(result.Items,s=>{Assert.Equal(f.Latest[s.ConfigVersion].Id,s.LatestReleaseId);Assert.Equal(s.ConfigVersion+1000,s.LatestDeploymentSequence);Assert.Equal("RolledBack",s.LatestDeploymentState);Assert.True(s.SizeBytes>0);Assert.Equal(64,s.PayloadHash.Length);});Assert.DoesNotContain(f.Sql.Templates,s=>s.Contains("payload_bytes",StringComparison.OrdinalIgnoreCase));
        }
    }
    [Theory]
    [InlineData("nodes")] [InlineData("events")] [InlineData("snapshots")]
    public async Task GatewayListsRetainEnvironmentAuthorizationAfterScopeProjection(string kind)
    {
        await using var f=new Fixture();await f.InitializeAsync();var foreign=new EnvironmentRecord{ProjectId=f.Api.Project.Id,Code="FOREIGN",Name="无权环境"};
        var foreignNode=new GatewayNode{EnvironmentId=foreign.Id,NodeName="foreign-scope",InstanceId="foreign",AppVersion="test"};
        await using(var db=f.Api.Context())
        {
            db.AddRange(foreign,foreignNode);var grant=await db.Set<UserProjectScope>().SingleAsync(x=>x.UserId==f.Actor.UserId);
            grant.ProjectId=f.Api.Project.Id;grant.EnvironmentId=f.Api.Environment.Id;await db.SaveChangesAsync();
        }
        async Task Read(bool other=false)
        {
            using var scope=f.Api.Services();var service=scope.ServiceProvider.GetRequiredService<GatewayReadService>();var env=other?foreign.Id:f.Api.Environment.Id;
            if(kind=="nodes")await service.NodesAsync(env,f.Actor);
            else if(kind=="events")await service.EventsAsync(other?foreignNode.Id:f.Nodes[0].Id,f.Actor);
            else await service.SnapshotsAsync(env,f.Actor);
        }
        await Read();Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>Read(true))).Status);
        await using(var db=f.Api.Context()){await db.Set<EnvironmentRecord>().Where(x=>x.Id==f.Api.Environment.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Disabled"));}
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>Read())).Status);
        await using(var db=f.Api.Context())
        {
            await db.Set<EnvironmentRecord>().Where(x=>x.Id==f.Api.Environment.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Active"));
            await db.Set<UserProjectScope>().Where(x=>x.UserId==f.Actor.UserId).ExecuteDeleteAsync();
        }
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>Read())).Status);
    }
    [Fact]
    public async Task ManagementSnapshotStillRedactsCredentialHashesAndDetectsCorruption()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.AddSnapshotsAsync(credential:true);using var scope=f.Api.Services();var service=scope.ServiceProvider.GetRequiredService<GatewayReadService>();
        var snapshot=await service.SnapshotAsync(f.Api.Environment.Id,1,f.Actor);Assert.Single(Assert.Single(snapshot.Applications).Credentials);Assert.Equal("ManagementViewNotRuntimePayload",snapshot.ExportType);var json=JsonSerializer.Serialize(snapshot,CanonicalJson.Options);Assert.DoesNotContain("\"hash\"",json);Assert.DoesNotContain("\"secret\"",json);
        await using var db=f.Api.Context();var id=await db.Set<GatewayConfigVersion>().Where(v=>v.EnvironmentId==f.Api.Environment.Id&&v.VersionNo==1).Select(v=>v.Id).SingleAsync();await db.Set<GatewayConfigSnapshot>().Where(s=>s.ConfigVersionId==id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.SizeBytes,1));Assert.Equal("corrupt_snapshot",(await Assert.ThrowsAsync<ApiException>(()=>service.SnapshotAsync(f.Api.Environment.Id,1,f.Actor))).Code);
    }
    [Theory]
    [InlineData("users")] [InlineData("nodes")] [InlineData("snapshots")]
    public async Task FullPrimaryReadAndPerSnapshotAssociationsAreMeasured(string kind)
    {
        await using var f=new Fixture();await f.InitializeAsync();if(kind=="snapshots")await f.AddSnapshotsAsync();using var services=f.Api.Services();f.Sql.Reset(kind);var request=Guid.NewGuid().ToString("N");using var measure=f.Probe.Begin(request);
        if(kind=="users"){var result=await services.ServiceProvider.GetRequiredService<GovernanceService>().UsersAsync(f.Actor,1,1);Assert.Single(result.Items);Assert.Equal(1,f.Sql.Associations);}
        else if(kind=="nodes")Assert.Single((await services.ServiceProvider.GetRequiredService<GatewayReadService>().NodesAsync(f.Api.Environment.Id,f.Actor,1,1)).Items);
        else Assert.Equal(50,(await services.ServiceProvider.GetRequiredService<GatewayReadService>().SnapshotsAsync(f.Api.Environment.Id,f.Actor,1,50)).Items.Count);
        Assert.True(f.Probe.Read(request).Rows>=151);if(kind=="snapshots")Assert.True(f.Sql.Data>=101);
    }
    [Fact]
    public async Task DuplicateLatestDeploymentStillFailsWithoutSelectingOne()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.AddSnapshotsAsync();var original=f.Latest[151];
        await using(var db=f.Api.Context()){await db.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_release_records_environment_id_deployment_sequence\"");db.Add(new ReleaseRecord{EnvironmentId=original.EnvironmentId,ReleaseNo="duplicate-latest-sequence",RequestedBy=original.RequestedBy,ToConfigVersion=original.ToConfigVersion,DeploymentSequence=original.DeploymentSequence,ReleaseType="publish",Status="Succeeded"});await db.SaveChangesAsync();}
        using var scope=f.Api.Services();await Assert.ThrowsAsync<ArgumentException>(()=>scope.ServiceProvider.GetRequiredService<GatewayReadService>().SnapshotsAsync(f.Api.Environment.Id,f.Actor,1,1));
    }
}
public sealed class CounterfactualGovernanceListTests_Releases
{
    private sealed class Capture:DbCommandInterceptor
    {
        internal int Data;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            if(new[]{"FROM release_records ","FROM approval_tasks ","FROM release_targets ","FROM gateway_acks ","FROM apis "}.Any(command.CommandText.Contains))Data++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private const int Seed=20261009;
        internal readonly PostgresDatabase Database=new();internal readonly Capture Sql=new();internal readonly RequestSqlProbe Probe=new();
        internal readonly string DirectoryPath=Path.Combine(Path.GetTempPath(),"release-list-"+Guid.NewGuid().ToString("N"));
        private WebApplication? app;internal readonly List<ReleaseRecord> Releases=[];
        internal Guid Id(string kind,int index)=>PerformanceDataset.ResourceId(Seed,kind,index);
        internal Guid EnvironmentId=>Id("environment",0);internal Guid OrganizationId=>Id("organization",0);internal Guid ProjectId=>Id("project",0);
        internal ActorContext Actor=>new(Id("user",0),"release-list");internal ScopeRef Scope=>new(OrganizationId,ProjectId,EnvironmentId);
        internal IServiceScope Services()=>app!.Services.CreateScope();
        internal async Task InitializeAsync()
        {
            if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Private fixtures require Unix modes.");Directory.CreateDirectory(DirectoryPath,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);await Database.InitializeAsync();await using var db=Database.Context();await db.Database.MigrateAsync();
            var now=DateTimeOffset.UtcNow.AddMinutes(-1);await PerformanceDataset.CreateAsync(db,new(100,Seed,now,now.AddHours(10)),DirectoryPath,CancellationToken.None);db.ChangeTracker.Clear();
            var source=await db.Set<ReleaseRecord>().AsNoTracking().OrderBy(r=>r.CreatedAt).FirstAsync();var flow=await db.Set<ApprovalFlow>().Select(f=>f.Id).SingleAsync();
            var candidate=JsonSerializer.Deserialize<FrozenReleaseCandidate>(source.CandidateBytes!,CanonicalJson.Options)!;
            var sourceHash=await db.Set<GatewayConfigVersion>().Where(v=>v.EnvironmentId==EnvironmentId&&v.VersionNo==source.ToConfigVersion).Select(v=>v.SnapshotHash).SingleAsync();
            var fromVersion=new ApiVersion{ApiId=candidate.Versions[0].Api.Id,Version="0.9.0",Status="Active",ChangeType="compatible",CreatedBy=Actor.UserId};db.Add(fromVersion);
            var approver=new Role{OrganizationId=OrganizationId,Code="QueryApprover",Name="冻结审批角色"};db.Add(approver);
            db.Add(new UserProjectScope{UserId=Id("user",3),OrganizationId=OrganizationId,AccessMode="read_write"});
            var failedId=Guid.NewGuid();
            for(var i=100;i<151;i++)
            {
                var state=(i%5) switch{0=>"WaitingApproval",1=>"Ready",2=>"Failed",3=>"RolledBack",_=>"Ready"};
                var release=new ReleaseRecord{Id=i==102?failedId:Guid.NewGuid(),EnvironmentId=EnvironmentId,ReleaseNo="QUERY-"+i,FromConfigVersion=source.FromConfigVersion,ToConfigVersion=source.ToConfigVersion,BaselineConfigVersion=source.BaselineConfigVersion,RequestedBy=state=="WaitingApproval"?Id("user",3):Actor.UserId,Status=state,ReleaseType=i%5==3?"rollback":i%5==4?"retry":"publish",RollbackOf=i%5==3?source.Id:null,RecoveryOf=i%5==4?failedId:null,FailureCode=state=="Failed"?"gateway_apply_failed":null,CreatedAt=now.AddSeconds(i),CandidateBytes=source.CandidateBytes,ApprovalPolicy=Encoding.UTF8.GetString(CanonicalJson.Serialize(new[]{new ApprovalRule(1,"QueryApprover",1),new ApprovalRule(2,"QueryApprover",1)}))};
                if(state is "Failed" or "RolledBack")release.DeploymentSequence=i+1;
                db.Add(release);
                for(var step=1;step<=2;step++)db.Add(new ApprovalTask{ReleaseId=release.Id,FlowId=flow,StepOrder=step,Status=state=="WaitingApproval"?"Pending":"Approved",AssigneeUserId=state=="WaitingApproval"?null:Id("user",step),Comment=state=="WaitingApproval"?null:"冻结审批意见",ActedAt=state=="WaitingApproval"?null:now});
                if(state is "Failed" or "RolledBack")for(var node=0;node<2;node++)
                {var entity=await db.Set<GatewayNode>().AsNoTracking().SingleAsync(n=>n.Id==Id("node",node));db.Add(new ReleaseTarget{ReleaseId=release.Id,NodeId=entity.Id,InstanceId=entity.InstanceId,CreatedAt=now});db.Add(new GatewayAck{ReleaseId=release.Id,NodeId=entity.Id,InstanceId=entity.InstanceId,ConfigVersion=release.ToConfigVersion,DeploymentSequence=release.DeploymentSequence!.Value,PayloadHash=sourceHash!,Success=state=="RolledBack",ErrorCode=state=="Failed"?"apply_failed":null,AppliedAt=now});}
            }
            await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var records=await db.Set<ReleaseRecord>().OrderByDescending(r=>r.CreatedAt).ThenByDescending(r=>r.Id).ToArrayAsync();Assert.Equal(151,records.Length);
            // Frozen historical risk references follow the existing inbox fixture contract;
            // the list must resolve actual Api IDs and current read rights, never trust the summary alone.
            foreach(var release in records)
            {
                var frozen=JsonSerializer.Deserialize<FrozenReleaseCandidate>(release.CandidateBytes!,CanonicalJson.Options)!;var version=frozen.Versions[0];var review=Guid.NewGuid();var comparison=Guid.NewGuid();
                var summary=new RiskReviewSummaryDto(review,comparison,version.Api.Id,fromVersion.Id,version.Version.Id,"0.9.0","1.0.0","AcceptedRisk",Actor.UserId,now,"input","report","engine","Partial",new(1,2,3,4,5,6),"PRIVATE_REVIEW_COMMENT");
                release.CandidateBytes=CanonicalJson.Serialize(frozen with{RiskReviewReferences=[new(version.Api.Id,version.Version.Id,comparison,review,"input","report","engine",summary)]});
            }
            var env=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==EnvironmentId);env.GatewayPublicUrl="https://current.example.test";env.BasePath="/new/";env.AccessAddressRevision=2;
            foreach(var release in records)db.Add(new ReleaseAccessContext{ReleaseId=release.Id,EnvironmentId=EnvironmentId,AccessAddressRevision=1,PublicOrigin="https://historical.example.test",InternalOrigin="http://internal.example.test:8080",BasePath="/old/",CapturedAt=now});
            await db.SaveChangesAsync();Releases.AddRange(records);
            app=ControlPlaneApp.Build(["--environment","Development"],b=>{b.Configuration["ConnectionStrings:WebApi"]=Database.ConnectionString;b.Logging.ClearProviders();LoginProtectionTestConfiguration.Apply(b,DirectoryPath,Guid.NewGuid().ToString("N"));b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(Sql,Probe));});
        }
        internal async Task<ActorContext> AddActorAsync(string name,bool approval=true,string mode="read_write",string? missingApiPermission=null,bool inactive=false,string roleCode="QueryApprover")
        {
            await using var db=Database.Context();var user=new UserRecord{Username="release-"+name,DisplayName=name,SecurityStamp=Guid.NewGuid().ToString("N"),Status=inactive?"Disabled":"Active"};var role=new Role{OrganizationId=OrganizationId,Code=name+"-"+Guid.NewGuid().ToString("N"),Name=name};db.AddRange(user,role);db.Add(new UserRole{UserId=user.Id,RoleId=role.Id});
            if(roleCode=="QueryApprover")db.Add(new UserRole{UserId=user.Id,RoleId=await db.Set<Role>().Where(r=>r.Code=="QueryApprover").Select(r=>r.Id).SingleAsync()});
            foreach(var permission in await db.Set<Permission>().Where(p=>p.Code=="release.read"||approval&&p.Code=="approval.act"||p.Code=="api.read"||p.Code=="api.version.read"||p.Code=="api.schema.read").ToArrayAsync())if(permission.Code!=missingApiPermission)db.Add(new RolePermission{RoleId=role.Id,PermissionId=permission.Id});
            db.Add(new UserProjectScope{UserId=user.Id,OrganizationId=OrganizationId,AccessMode=mode});await db.SaveChangesAsync();return new(user.Id,name);
        }
        internal async Task<(PageResult<ReleaseDto> Page,int Data)> ReadAsync(ActorContext actor,int page,int size)
        {
            Sql.Data=0;var id=Guid.NewGuid().ToString("N");using var probe=Probe.Begin(id);using var services=Services();var result=await services.ServiceProvider.GetRequiredService<ReleaseService>().ListAsync(EnvironmentId,actor,page,size);
            Assert.InRange(Sql.Data,1,12);Assert.True(Probe.Read(id).Total<=40,$"total SQL {Probe.Read(id).Total} > 40");return(result,Sql.Data);
        }
        public async ValueTask DisposeAsync(){if(app is not null)await app.DisposeAsync();await Database.DisposeAsync();if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true);}
    }
    [Fact]
    public async Task ReleaseListMatchesDetailFieldsWithConstantSql()
    {
        await using var f=new Fixture();await f.InitializeAsync();using var services=f.Services();var service=services.ServiceProvider.GetRequiredService<ReleaseService>();
        var details=new Dictionary<Guid,ReleaseDto>();foreach(var release in f.Releases)details[release.Id]=await service.GetAsync(release.Id,f.Actor);
        int? pageSql=null;
        foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2})
        {
            var result=await f.ReadAsync(f.Actor,page,size);Assert.Equal(151,result.Page.Total);Assert.Equal(f.Releases.Skip((page-1)*size).Take(size).Select(r=>r.Id),result.Page.Items.Select(r=>r.Id));
            foreach(var item in result.Page.Items)Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(details[item.Id],CanonicalJson.Options),JsonSerializer.SerializeToElement(item,CanonicalJson.Options)),"Full release DTO differs from detail oracle");
            if(result.Page.Items.Count>0){pageSql??=result.Data;Assert.Equal(pageSql,result.Data);}
        }
        var empty=await f.ReadAsync(f.Actor,1000001,100);Assert.Equal(1000000,empty.Page.Page);Assert.Equal(151,empty.Page.Total);Assert.Empty(empty.Page.Items);
        var json=JsonSerializer.Serialize(details.Values,CanonicalJson.Options);Assert.DoesNotContain("secretHash",json);Assert.DoesNotContain("candidateBytes",json);Assert.Contains("PRIVATE_REVIEW_COMMENT",json);
    }
    [Fact]
    public async Task EligibilityPreservesErrorPrecedenceAndIndependence()
    {
        await using var f=new Fixture();await f.InitializeAsync();var waiting=f.Releases.First(r=>r.Status=="WaitingApproval");var used=await f.AddActorAsync("used");
        await using(var db=f.Database.Context()){var task=await db.Set<ApprovalTask>().SingleAsync(t=>t.ReleaseId==waiting.Id&&t.StepOrder==1);task.Status="Approved";task.AssigneeUserId=used.UserId;task.ActedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}
        var cases=new[]{(await f.AddActorAsync("no-function",approval:false),waiting,new ApprovalEligibility(false,null,"permission_denied")),(await f.AddActorAsync("readonly",mode:"read"),waiting,new ApprovalEligibility(false,null,"scope_denied")),(new ActorContext(f.Id("user",3),"self"),waiting,new ApprovalEligibility(false,2,"self_approval")),(used,waiting,new ApprovalEligibility(false,2,"independent_approval_required")),(await f.AddActorAsync("wrong-role",roleCode:"Wrong"),waiting,new ApprovalEligibility(false,2,"approval_role_required")),(await f.AddActorAsync("valid"),waiting,new ApprovalEligibility(true,2,null)),(f.Actor,f.Releases.First(r=>r.Status=="Succeeded"),new ApprovalEligibility(false,null,"invalid_release_state"))};
        foreach(var(actor,release,expected)in cases){using var scope=f.Services();var service=scope.ServiceProvider.GetRequiredService<ApprovalEligibilityService>();Assert.Equal(expected,await service.EvaluateAsync(release,f.Scope,actor,CancellationToken.None));var batch=await service.EvaluateBatchAsync([release],f.Scope,actor,CancellationToken.None);Assert.Single(batch);Assert.Equal(expected,batch[release.Id]);}
        var inactive=await f.AddActorAsync("inactive",inactive:true);using var services=f.Services();var eligibility=services.ServiceProvider.GetRequiredService<ApprovalEligibilityService>();Assert.Equal(401,(await Assert.ThrowsAsync<ApiException>(()=>eligibility.EvaluateBatchAsync([waiting],f.Scope,inactive,CancellationToken.None))).Status);
    }
    [Theory]
    [InlineData(null)] [InlineData("api.read")] [InlineData("api.version.read")] [InlineData("api.schema.read")]
    public async Task RiskCommentRequiresAllThreePermissions(string? missing)
    {
        await using var f=new Fixture();await f.InitializeAsync();var actor=await f.AddActorAsync("risk-reader",missingApiPermission:missing);var result=await f.ReadAsync(actor,1,100);
        Assert.Equal(151,result.Page.Total);Assert.Equal(100,result.Page.Items.Count);Assert.All(result.Page.Items,r=>{var risk=Assert.Single(r.FrozenCandidate!.RiskReviews!);Assert.Equal(missing is null?"PRIVATE_REVIEW_COMMENT":null,risk.Comment);});
        using var services=f.Services();foreach(var release in result.Page.Items.Take(2)){var detail=await services.ServiceProvider.GetRequiredService<ReleaseService>().GetAsync(release.Id,actor);Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(detail,CanonicalJson.Options),JsonSerializer.SerializeToElement(release,CanonicalJson.Options)));}
    }
    [Fact]
    public async Task ReleaseAndUserRelationshipsRemainByteIdentical()
    {
        await using var f=new Fixture();await f.InitializeAsync();var single=await f.ReadAsync(f.Actor,1,1);var page=await f.ReadAsync(f.Actor,1,100);Assert.NotNull(single.Page.Items[0].AccessContext);Assert.Equal("https://historical.example.test",single.Page.Items[0].AccessContext!.PublicOrigin);Assert.Equal("https://current.example.test",single.Page.Items[0].CurrentPublicOrigin);Assert.Equal("/new/",single.Page.Items[0].CurrentBasePath);Assert.True(single.Page.Items[0].AccessAddressChanged);Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(single.Page.Items[0],CanonicalJson.Options),JsonSerializer.SerializeToElement(page.Page.Items[0],CanonicalJson.Options)));
        const string request="full-release-main";using var probe=f.Probe.Begin(request);using var scope=f.Services();var result=await scope.ServiceProvider.GetRequiredService<ReleaseService>().ListAsync(f.EnvironmentId,f.Actor,1,1);Assert.Equal(151,result.Total);Assert.Single(result.Items);Assert.True(f.Probe.Read(request).Rows>=151);
    }
}

public sealed class CounterfactualReleaseRelationshipEvidenceTests
{
 [Theory][InlineData(false)][InlineData(true)]
 public async Task NonEmptyDeliveryPipelineAndAddressFieldsRetainFullBytesAndPermissionMask(bool restricted)
 {
  await using var f=new PipelinePromotionFixture();await f.InitializeAsync(2);var requester=await PipelineExecutionTests.ActorAsync(f);var promotion=await PipelineExecutionTests.SubmittedAsync(f,requester.Client);var api=f.Scenario.Api;
  await using(var db=api.Context()){var role=await db.Set<Role>().SingleAsync(r=>r.Code=="ApiApprover"&&r.OrganizationId==api.Organization.Id);db.Add(new UserRole{UserId=api.User.Id,RoleId=role.Id});await db.SaveChangesAsync();}
  if(!restricted)await f.Scenario.GrantAsync(api.User.Id,"environment.write");
  await using(var db=api.Context()){
   var environment=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==f.TargetEnvironmentId);environment.GatewayPublicUrl="https://current.example.test";environment.BasePath="/new/";environment.AccessAddressRevision=2;
   db.Add(new ReleaseAccessContext{ReleaseId=promotion.TargetReleaseId!.Value,EnvironmentId=f.TargetEnvironmentId,AccessAddressRevision=1,PublicOrigin="https://historical.example.test",InternalOrigin="http://internal.example.test:8080",BasePath="/old/",CapturedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();
   if(restricted){var roles=await db.Set<UserRole>().Where(r=>r.UserId==api.User.Id).Select(r=>r.RoleId).ToArrayAsync();var permission=await db.Set<Permission>().SingleAsync(p=>p.Code=="environment.write");await db.Set<RolePermission>().Where(r=>roles.Contains(r.RoleId)&&r.PermissionId==permission.Id).ExecuteDeleteAsync();await db.Set<UserProjectScope>().Where(r=>r.UserId==api.User.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ProjectId,f.Run.ProjectId).SetProperty(r=>r.EnvironmentId,f.TargetEnvironmentId));}
  }
  using var scope=api.Services();var service=scope.ServiceProvider.GetRequiredService<ReleaseService>();var actor=new ActorContext(api.User.Id,"relationship-evidence");
  foreach(var size in new[]{1,50,100}){
   var page=await service.ListAsync(f.TargetEnvironmentId,actor,1,size);var row=page.Items.Single(r=>r.Id==promotion.TargetReleaseId);var detail=await service.GetAsync(row.Id,actor);Assert.Equal(CanonicalJson.Serialize(detail),CanonicalJson.Serialize(row));
   Assert.NotNull(row.AccessContext);Assert.Equal("https://historical.example.test",row.AccessContext.PublicOrigin);Assert.Equal(restricted?null:"http://internal.example.test:8080",row.AccessContext.InternalOrigin);Assert.Equal("/old/",row.AccessContext.BasePath);Assert.Equal("https://current.example.test",row.CurrentPublicOrigin);Assert.Equal("/new/",row.CurrentBasePath);Assert.True(row.AccessAddressChanged);
   Assert.NotNull(row.Delivery);Assert.Equal(restricted?"Restricted":"Visible",row.Delivery.Visibility);if(restricted){Assert.Null(row.Delivery.PromotionId);Assert.Null(row.Delivery.ArtifactId);Assert.Null(row.Delivery.ArtifactHash);Assert.Null(row.Delivery.SourceEnvironment);Assert.Null(row.Delivery.Pipeline);}else{Assert.Equal(promotion.Id,row.Delivery.PromotionId);Assert.Equal(promotion.ArtifactId,row.Delivery.ArtifactId);Assert.Equal(promotion.ArtifactHash,row.Delivery.ArtifactHash);Assert.Equal(promotion.SourceEnvironmentId,row.Delivery.SourceEnvironment!.Id);Assert.NotNull(row.Delivery.Pipeline);Assert.Equal(f.Run.Id,row.Delivery.Pipeline.RunId);Assert.Equal(f.TargetStageId,row.Delivery.Pipeline.StageId);Assert.Equal(2,row.Delivery.Pipeline.Order);Assert.Equal("Full",row.Delivery.Pipeline.Visibility);}
   Assert.Equal("pipeline_run_creator_approval",row.ApprovalEligibility!.ReasonCode);Assert.False(row.ApprovalEligibility.CanAct);
  }
 }
}

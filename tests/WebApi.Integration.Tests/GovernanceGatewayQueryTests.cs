using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
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

namespace WebApi.Integration.Tests;

public sealed class GovernanceGatewayQueryTests
{
    private sealed class Capture : DbCommandInterceptor
    {
        internal string Kind="";internal int Data;internal int Associations;internal readonly List<string> Templates=[];
        internal void Reset(string kind){Kind=kind;Data=Associations=0;Templates.Clear();}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {
            var sql=command.CommandText;Templates.Add(sql);
            var relation=Kind switch {"users"=>new[]{"FROM users ","FROM user_roles "},"nodes"=>new[]{"FROM gateway_nodes "},"events"=>new[]{"FROM gateway_node_events "},_=>new[]{"FROM gateway_config_versions ","FROM gateway_config_snapshots ","FROM release_records "}};
            // Actor existence, functional-role joins and the node detail prerequisite are authorization.
            if(!sql.StartsWith("SELECT EXISTS",StringComparison.Ordinal)&&!sql.Contains("INNER JOIN roles ")&&relation.Any(sql.Contains))Data++;
            if(Kind=="users"&&sql.Contains("FROM user_roles ")&&!sql.Contains("INNER JOIN roles "))Associations++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ApiFixture Api=new();internal readonly Capture Sql=new();internal readonly RequestSqlProbe Probe=new();internal readonly List<UserRecord> Users=[];internal readonly List<GatewayNode> Nodes=[];internal readonly Dictionary<Guid,Guid[]> Roles=[];internal readonly Dictionary<long,ReleaseRecord> Latest=[];
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
            Sql.Reset(kind);var id=Guid.NewGuid().ToString("N");using var probe=Probe.Begin(id);using var scope=Api.Services();var result=await action(scope.ServiceProvider);var stats=Probe.Read(id);
            Assert.InRange(Sql.Data,1,budget);Assert.True(stats.Total<=16,$"totalSQL {stats.Total} > 16");Assert.True(stats.Rows<=rows,$"consumed {stats.Rows} rows > {rows}");return result;
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
            Assert.Equal(151,result.Total);Assert.Equal(expected,result.Items.Select(u=>u.Id));Assert.Equal(Math.Min(page,1000000),result.Page);Assert.All(result.Items,u=>Assert.Equal(f.Roles[u.Id].Order(),u.RoleIds.Order()));Assert.Equal(expected.Length==0?0:1,f.Sql.Associations);
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
        var last=await f.Measure("events",2,112,p=>p.GetRequiredService<GatewayReadService>().EventsAsync(f.Nodes[0].Id,f.Actor,100,100));Assert.Equal(10000,last.Total);Assert.Equal(Enumerable.Range(4,100).Reverse().Select(i=>(long)i),last.Items.Select(e=>e.Id));
        var first=await f.Measure("events",2,13,p=>p.GetRequiredService<GatewayReadService>().EventsAsync(f.Nodes[0].Id,f.Actor,1,1));Assert.Equal(10003,Assert.Single(first.Items).Id);
        var empty=await f.Measure("events",2,12,p=>p.GetRequiredService<GatewayReadService>().EventsAsync(f.Nodes[0].Id,f.Actor,1000001,100));Assert.Empty(empty.Items);Assert.Equal(10000,empty.Total);Assert.Equal(1000000,empty.Page);
        await using(var db=f.Api.Context()){db.AddRange(Enumerable.Range(20000,10003).Select(i=>new GatewayNodeEvent{Id=i,GatewayNodeId=f.Nodes[1].Id,EventType="Other",Message="unrelated"}));await db.SaveChangesAsync();}
        var unchanged=await f.Measure("events",2,112,p=>p.GetRequiredService<GatewayReadService>().EventsAsync(f.Nodes[0].Id,f.Actor,100,100));Assert.Equal(last.Total,unchanged.Total);Assert.Equal(last.Items,unchanged.Items);
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
    [Fact]
    public async Task ManagementSnapshotStillRedactsCredentialHashesAndDetectsCorruption()
    {
        await using var f=new Fixture();await f.InitializeAsync();await f.AddSnapshotsAsync(credential:true);using var scope=f.Api.Services();var service=scope.ServiceProvider.GetRequiredService<GatewayReadService>();
        var snapshot=await service.SnapshotAsync(f.Api.Environment.Id,1,f.Actor);Assert.Single(Assert.Single(snapshot.Applications).Credentials);Assert.Equal("ManagementViewNotRuntimePayload",snapshot.ExportType);var json=JsonSerializer.Serialize(snapshot,CanonicalJson.Options);Assert.DoesNotContain("\"hash\"",json);Assert.DoesNotContain("\"secret\"",json);
        await using var db=f.Api.Context();var id=await db.Set<GatewayConfigVersion>().Where(v=>v.EnvironmentId==f.Api.Environment.Id&&v.VersionNo==1).Select(v=>v.Id).SingleAsync();await db.Set<GatewayConfigSnapshot>().Where(s=>s.ConfigVersionId==id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.SizeBytes,1));Assert.Equal("corrupt_snapshot",(await Assert.ThrowsAsync<ApiException>(()=>service.SnapshotAsync(f.Api.Environment.Id,1,f.Actor))).Code);
    }
}

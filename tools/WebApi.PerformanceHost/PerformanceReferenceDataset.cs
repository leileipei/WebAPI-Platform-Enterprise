using WebApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Routing;
namespace WebApi.PerformanceHost;
public sealed record ReferenceActor(string Kind,string Username,IReadOnlyList<PolicyReferenceDto> Expected);
public sealed record ReferenceSceneManifest(int Scale,Guid PolicyId,int Working,int FrozenRelease,int Runtime,int AdminTotal,int EnvironmentReaderTotal,long CandidateBytes,long RuntimeBytes,string BusinessFingerprint,bool Validated,IReadOnlyList<ReferenceActor>? Actors=null);
public static partial class PerformanceDataset
{
    public static async Task<ReferenceSceneManifest> CreateReferenceSceneAsync(WebApiDbContext db,DatasetSpec spec,string directory,CancellationToken ct)
    {
        var initial=await CreateAsync(db,spec,directory,ct);
        Guid Id(string kind,int index)=>ResourceId(spec.Seed,kind,index);
        var env=Id("environment",0);var user=Id("user",0);var policy=Id("policy",0);var project=Id("project",0);var org=Id("organization",0);var flow=Id("flow",0);var now=spec.WindowStart;
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        var releaseId=Id("release",spec.Scale-1);
        var original=await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==releaseId,ct);
        var template=JsonSerializer.Deserialize<FrozenReleaseCandidate>(original.CandidateBytes!,CanonicalJson.Options)!;
        var pending=template with {BaselineConfigVersion=spec.Scale};var pendingBytes=CanonicalJson.Serialize(pending);
        var compiler=new SnapshotCompiler(new UpstreamAddressPolicy(["http://backend:8080"]));
        var currentBytes=await (from version in db.Set<GatewayConfigVersion>() join snapshot in db.Set<GatewayConfigSnapshot>() on version.Id equals snapshot.ConfigVersionId where version.EnvironmentId==env&&version.VersionNo==spec.Scale select snapshot.PayloadBytes).SingleAsync(ct);
        compiler.Compile(pending,SnapshotValidator.ParsePayload(currentBytes,env),spec.Scale+1,now);
        var expected=new List<PolicyReferenceDto>();
        for(var i=0;i<spec.Scale;i++)expected.Add(new("Working",env,Id("api",i),Id("route",i),null,1,$"route{i:D5}"));
        var runtimeHashes=new Dictionary<string,string>();long runtimeBytes=currentBytes.Length;
        runtimeHashes[env.ToString()]=Convert.ToHexStringLower(SHA256.HashData(currentBytes));
        // The real compiler folds ApiKey policies into schema 2.0, which omits provenance.
        expected.Add(new("Runtime",env,null,null,spec.Scale,null,$"配置 v{spec.Scale}"));
        for(var environmentIndex=1;environmentIndex<=2;environmentIndex++) {
            var other=Id("environment",environmentIndex);
            var clusters=template.Clusters.Select((cluster,index)=>cluster with {Id=Id("reference-cluster",environmentIndex*8+index),EnvironmentId=other,Destinations=cluster.Destinations.Select((destination,j)=>destination with {Id=Id("reference-destination",environmentIndex*16+index*2+j),ClusterId=Id("reference-cluster",environmentIndex*8+index)}).ToArray()}).ToArray();
            var clusterMap=template.Clusters.Select((c,index)=>(c.Id,Replacement:clusters[index].Id)).ToDictionary(x=>x.Id,x=>x.Replacement);
            var routes=template.Routes.Select((route,index)=>route with {Id=Id("reference-route",environmentIndex*8+index),EnvironmentId=other,ClusterId=clusterMap[route.ClusterId]}).ToArray();
            var apps=template.Applications.Select(app=>app with {Permissions=app.Permissions.Select((grant,index)=>grant with {Id=Id("reference-grant",environmentIndex*8+index),EnvironmentId=other}).ToArray()}).ToArray();
            var candidate=template with {EnvironmentId=other,BaselineConfigVersion=0,Clusters=clusters,Routes=routes,Bindings=routes.Select(r=>new FrozenBinding(r.Id,policy,0)).ToArray(),Applications=apps};
            var compiled=compiler.Compile(candidate,new RuntimeSnapshot("2.0",other,0,now,[],[],[],[]),1,now);SnapshotValidator.ParsePayload(compiled.Payload.ToArray(),other);
            runtimeBytes+=compiled.Size;runtimeHashes[other.ToString()]=compiled.Hash;
            foreach(var cluster in clusters){db.Add(new UpstreamCluster {Id=cluster.Id,ProjectId=project,EnvironmentId=other,Name=cluster.Name,LoadBalancingPolicy=cluster.LoadBalancingPolicy,HealthCheckEnabled=cluster.HealthCheckEnabled,HealthCheckPath=cluster.HealthCheckPath,HealthCheckIntervalSec=cluster.HealthCheckIntervalSec});foreach(var destination in cluster.Destinations)db.Add(new UpstreamDestination {Id=destination.Id,ClusterId=cluster.Id,Name=destination.Name,Address=destination.Address,Weight=destination.Weight,CreatedAt=now});}
            foreach(var route in routes){db.Add(new ApiRoute {Id=route.Id,ApiVersionId=route.ApiVersionId,EnvironmentId=other,RouteName=route.RouteName,Path=route.Path,NormalizedPath=route.NormalizedPath,Methods=route.Methods.ToArray(),ClusterId=route.ClusterId,TimeoutMs=route.TimeoutMs,CreatedAt=now,UpdatedAt=now});db.Add(new RouteMethod {RouteId=route.Id,EnvironmentId=other,Method="GET",NormalizedPath=route.NormalizedPath});db.Add(new RoutePolicyBinding {RouteId=route.Id,PolicyId=policy,Priority=0});var api=template.Versions.Single(v=>v.Version.Id==route.ApiVersionId).Api.Id;expected.Add(new("Working",other,api,route.Id,null,1,route.RouteName));}
            foreach(var app in apps)foreach(var grant in app.Permissions)db.Add(new ApplicationApiPermission {Id=grant.Id,ApplicationId=grant.ApplicationId,ApiId=grant.ApiId,EnvironmentId=other,ValidFrom=grant.ValidFrom,ExpiresAt=grant.ExpiresAt,CreatedAt=now,CreatedBy=user});
            var config=new GatewayConfigVersion {Id=Id("reference-config",environmentIndex),EnvironmentId=other,VersionNo=1,Status="Published",SnapshotHash=compiled.Hash,CreatedBy=user,CreatedAt=now,PublishedAt=now};db.Add(config);db.Add(new GatewayConfigSnapshot {ConfigVersionId=config.Id,Payload=Encoding.UTF8.GetString(compiled.Payload.ToArray()),PayloadBytes=compiled.Payload.ToArray(),SizeBytes=compiled.Size,CreatedAt=now});
            var environment=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==other,ct);environment.DesiredConfigVersion=1;environment.DeploymentSequence=1;
            db.Add(new GatewayNode {Id=Id("reference-node",environmentIndex),EnvironmentId=other,NodeName=$"reference-node-{environmentIndex}",InstanceId=$"reference-instance-{environmentIndex}",AppVersion="performance-history",CurrentConfigVersion=1,CurrentDeploymentSequence=1,LastHeartbeatAt=now,IdentityHash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"reference-node:{spec.Seed}:{environmentIndex}")))});
            expected.Add(new("Runtime",other,null,null,1,null,"配置 v1"));
        }
        var readerId=Id("user",2);var reader=await db.Set<UserRecord>().SingleAsync(u=>u.Id==readerId,ct);var adminId=user;reader.PasswordHash=await db.Set<UserRecord>().Where(u=>u.Id==adminId).Select(u=>u.PasswordHash).SingleAsync(ct);
        db.Add(new UserProjectScope {Id=Id("reference-scope",2),UserId=readerId,OrganizationId=org,ProjectId=project,EnvironmentId=env,AccessMode="read_only"});
        await db.SaveChangesAsync(ct);db.ChangeTracker.Clear();
        for(var i=0;i<spec.Scale;i++) {
            var pendingId=Id("reference-release",i);var name=$"PERF-PENDING-{i:D5}";
            db.Add(new ReleaseRecord {Id=pendingId,EnvironmentId=env,ReleaseNo=name,FromConfigVersion=spec.Scale,ToConfigVersion=0,BaselineConfigVersion=spec.Scale,ReleaseType="publish",Status="WaitingApproval",RequestedBy=user,CreatedAt=now,DeadlineAt=spec.WindowEnd,CandidateBytes=pendingBytes,ApprovalPolicy=original.ApprovalPolicy});
            for(var step=1;step<=2;step++)db.Add(new ApprovalTask {Id=Id("reference-task",i*2+step-1),FlowId=flow,ReleaseId=pendingId,StepOrder=step,Status="Pending"});
            expected.Add(new("FrozenRelease",env,null,null,null,1,name));
            if((i+1)%200==0){await db.SaveChangesAsync(ct);db.ChangeTracker.Clear();}
        }
        await db.SaveChangesAsync(ct);db.ChangeTracker.Clear();await transaction.CommitAsync(ct);
        var ordered=expected.OrderBy(r=>r.ReferenceKind).ThenBy(r=>r.EnvironmentId).ThenBy(r=>r.Name).ToArray();
        var visible=ordered.Where(r=>r.EnvironmentId==env).ToArray();
        var actors=new[]{new ReferenceActor("platform-role","performance_admin",ordered),new ReferenceActor("environment-read-only","perf_user_00002",visible)};
        var fingerprint=Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(new {initial.BusinessFingerprint,actors,runtimeHashes,pendingHash=Convert.ToHexStringLower(SHA256.HashData(pendingBytes))})));
        return new(spec.Scale,policy,spec.Scale+16,spec.Scale,3,ordered.Length,visible.Length,(long)pendingBytes.Length*spec.Scale,runtimeBytes,fingerprint,true,actors);
    }
}

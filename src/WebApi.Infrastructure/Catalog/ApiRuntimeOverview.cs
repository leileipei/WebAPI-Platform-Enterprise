using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Catalog;

public sealed record ApiClusterHealth(Guid ClusterId, string Health);
public sealed record ApiPolicySummary(Guid PolicyId, string Type, long? Revision);
public sealed record ApiRoutePolicySummary(Guid RouteId,string Path,string Authentication,int TimeoutMs);
public sealed record ApiRecentRelease(Guid Id, string ReleaseNo, string Status, string ReleaseType, DateTimeOffset CreatedAt);
public sealed record ApiOverviewDto(Guid ApiId, Guid EnvironmentId, string RunningState, long? ConfigVersion,
    IReadOnlyList<ApiPolicySummary>? Policies,IReadOnlyList<ApiRoutePolicySummary>? RoutePolicies,IReadOnlyList<ApiClusterHealth>? Clusters, IReadOnlyList<ApiRecentRelease>? Releases);

internal sealed record ApiRuntimeState(RuntimeSnapshot? Snapshot, IReadOnlyDictionary<Guid,string> Health)
{
    internal ApiClusterHealth[] Clusters(Guid apiId)
    {
        if(Snapshot is null)return [];
        var ids=Snapshot.Routes.Where(r=>r.ApiId==apiId).Select(r=>r.ClusterId).ToHashSet();
        return Snapshot.Clusters.Where(c=>ids.Contains(c.Id)).GroupBy(c=>c.SourceId??c.Id)
            .Select(g=>new ApiClusterHealth(g.Key,Aggregate(g.Select(c=>Health.GetValueOrDefault(c.Id,"Unknown"))))).OrderBy(c=>c.ClusterId).ToArray();
    }

    internal static string Aggregate(IEnumerable<string> health)
    {
        var states=health.ToHashSet();
        if(states.Contains("Unhealthy"))return "Unhealthy";
        if(states.Count==0||states.Contains("Unknown"))return "Unknown";
        return states.Contains("Healthy")?"Healthy":"Disabled";
    }
}

public sealed class ApiRuntimeOverview(WebApiDbContext db, ScopeResolver scopes, AuthorizationService auth,
    RunningDeploymentReader deployments, ClusterHealthReadService probes, CatalogService catalog)
{
    internal async Task<EnvironmentRecord> EnvironmentAsync(Guid projectId,Guid environmentId,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.EnvironmentAsync(environmentId,ct);
        if(scope.ProjectId!=projectId||!await auth.CanAsync(actor,"api.read",new("environment",environmentId,scope),ct))throw ScopeResolver.Missing();
        return await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==environmentId,ct);
    }

    internal async Task<ApiRuntimeState> RuntimeAsync(EnvironmentRecord environment,bool readHealth,CancellationToken ct,Guid? apiId=null)
    {
        RuntimeSnapshot snapshot;
        try {snapshot=(await deployments.ReadSnapshotAsync(environment,ct)).Snapshot;}
        catch(ApiException e) when(e.Code=="running_state_unconfirmed"){return new(null,new Dictionary<Guid,string>());}
        var health=new Dictionary<Guid,string>();
        if(!readHealth||snapshot.Clusters.Count==0)return new(snapshot,health);
        // Match the exact target addresses and check paths from the confirmed runtime snapshot, even after draft edits.
        var related=apiId.HasValue?snapshot.Routes.Where(r=>r.ApiId==apiId).Select(r=>r.ClusterId).ToHashSet():null;
        var clusters=snapshot.Clusters.Where(c=>related==null||related.Contains(c.Id)).ToArray();
        foreach(var batch in clusters.Chunk(64)){
            var inputs=batch.Select(c=>new ClusterDto(c.SourceId??c.Id,environment.ProjectId,environment.Id,"",c.LoadBalancingPolicy,
                c.HealthCheckEnabled,c.HealthCheckPath,c.HealthCheckIntervalSec,"Active",0,
                c.Destinations.Select(d=>new DestinationDto(d.Id,c.SourceId??c.Id,"",d.Address,d.Weight,true,null,0)).ToArray())).ToArray();
            var verified=await probes.ReadAsync(environment.Id,inputs,ct);
            for(var i=0;i<verified.Length;i++){
                var c=verified[i];
                var states=c.Destinations.SelectMany(d=>new[]{d.ActiveHealth,d.PassiveHealth}).ToArray();
                health[batch[i].Id]=states.Contains("Unhealthy")?"Unhealthy":
                    c.Destinations.Count>0&&c.HealthyDestinationCount==c.Destinations.Count?"Healthy":
                    states.Length>0&&states.All(s=>s=="Disabled")?"Disabled":"Unknown";
            }
        }
        // The metrics provider may have taken time; avoid attributing those probes to a newly replaced snapshot.
        try {
            var latest=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==environment.Id,ct);
            if(latest.DeploymentSequence!=environment.DeploymentSequence||latest.DesiredConfigVersion!=snapshot.ConfigVersion)
                return new(null,new Dictionary<Guid,string>());
            await deployments.ReadSnapshotAsync(latest,ct);
        } catch(ApiException e) when(e.Code=="running_state_unconfirmed"){return new(null,new Dictionary<Guid,string>());}
        return new(snapshot,health);
    }

    public async Task<ApiOverviewDto> GetAsync(Guid apiId,Guid environmentId,ActorContext actor,CancellationToken ct)
    {
        var project=await catalog.ReadAsync(apiId,"api.read",actor,ct);
        var environment=await EnvironmentAsync(project.ProjectId!.Value,environmentId,actor,ct);
        var scope=project with {EnvironmentId=environmentId};
        Task<bool> Can(string permission)=>auth.CanAsync(actor,permission,new("environment",environmentId,scope),ct);
        var routeRead=await Can("route.read");
        var policyRead=routeRead&&await Can("policy.read");
        var healthRead=routeRead&&await Can("cluster.read")&&await Can("metrics.read");
        var runtime=policyRead||healthRead?await RuntimeAsync(environment,healthRead,ct,apiId):null;
        var snapshot=runtime?.Snapshot;
        IReadOnlyList<ApiPolicySummary>? policies=null;
        ApiRoutePolicySummary[]? routePolicies=null;
        if(policyRead&&await Can("route.read")&&await Can("policy.read")){
            var routes=snapshot?.Routes.Where(r=>r.ApiId==apiId).ToArray()??[];
            routePolicies=routes.OrderBy(r=>r.Path).ThenBy(r=>r.Id).Select(r=>new ApiRoutePolicySummary(r.Id,r.Path,r.AuthenticationMode?.ToString()??(r.RequireApiKey?"ApiKey":"Anonymous"),r.TimeoutMs)).ToArray();
            var policyIds=routes.SelectMany(r=>r.PolicyBindings??[]).Select(b=>b.PolicyId).ToHashSet();
            policies=snapshot?.Policies.Where(p=>policyIds.Contains(p.Id)).Select(p=>new ApiPolicySummary(p.SourcePolicyId??p.Id,p.Type,p.SourceRevision)).Distinct().OrderBy(p=>p.Type).ThenBy(p=>p.PolicyId).ToArray()??[];
        }
        var clusters=healthRead&&await Can("route.read")&&await Can("metrics.read")&&await Can("cluster.read") ? runtime?.Clusters(apiId):null;
        var releases=await Can("release.read")?await catalog.RecentReleasesAsync(apiId,environmentId,ct):null;
        if(!await Can("api.read"))throw ScopeResolver.Missing();
        if(releases!=null&&!await Can("release.read"))releases=null;
        return new(apiId,environmentId,policies is null&&clusters is null?"Restricted":snapshot is null?"Unconfirmed":"Confirmed",policies is null&&clusters is null?null:snapshot?.ConfigVersion,policies,routePolicies,clusters,releases);
    }
}

using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Observability;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Observability;
public sealed class ObservationScopeResolver(WebApiDbContext db,AuthorizationService authorization)
{
    public async Task<TrustedObservationScope> ResolveAsync(ActorContext actor,string permission,ObservationScopeRequest scope,Guid? apiId,Guid? appId,Guid? destinationId,CancellationToken ct)
    {
        var project=await db.Set<Project>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==scope.ProjectId&&x.OrganizationId==scope.OrganizationId,ct)??throw ScopeResolver.Missing();
        Guid[] environments;
        if(scope.AllAccessibleEnvironments)
        {
            var projectGranted=true;
            // This checks the functional permission even when this actor only holds environment-level grants.
            try{await authorization.RequireAsync(actor,permission,new("observation",project.Id,new(scope.OrganizationId,scope.ProjectId)),ct);}
            catch(ApiException e)when(e.Code=="scope_denied"){projectGranted=false;}
            if(project.Status!="Active"||!await db.Set<Organization>().AnyAsync(x=>x.Id==scope.OrganizationId&&x.Status=="Active",ct))throw new ApiException(409,"inactive_scope","组织或项目已停用。");
            var candidates=await db.Set<EnvironmentRecord>().AsNoTracking().Where(x=>x.ProjectId==project.Id&&x.Status=="Active").Select(x=>x.Id).ToArrayAsync(ct);
            var allowed=new List<Guid>();foreach(var environment in candidates)if(await authorization.CanAsync(actor,permission,new("observation",environment,new(scope.OrganizationId,scope.ProjectId,environment)),ct))allowed.Add(environment);
            environments=allowed.Order().ToArray();
            if(environments.Length==0&&!projectGranted)throw ScopeResolver.Missing();
        }
        else
        {
            if(scope.EnvironmentId is not Guid environment||!await db.Set<EnvironmentRecord>().AnyAsync(x=>x.Id==environment&&x.ProjectId==project.Id,ct))throw ScopeResolver.Missing();
            try{await authorization.RequireAsync(actor,permission,new("observation",environment,new(scope.OrganizationId,scope.ProjectId,environment)),ct);}
            catch(ApiException e)when(e.Code=="scope_denied"){throw ScopeResolver.Missing();}
            environments=[environment];
        }
        var apis=await db.Set<Api>().AsNoTracking().Where(x=>x.OrganizationId==scope.OrganizationId&&x.ProjectId==project.Id).ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);
        var apps=await db.Set<ApplicationRecord>().AsNoTracking().Where(x=>x.OrganizationId==scope.OrganizationId&&x.ProjectId==project.Id).ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);
        var destinations=await(from d in db.Set<UpstreamDestination>().AsNoTracking() join c in db.Set<UpstreamCluster>().AsNoTracking() on d.ClusterId equals c.Id where c.ProjectId==project.Id&&environments.Contains(c.EnvironmentId) select new ObservationDestination(d.Id,c.Id,c.EnvironmentId,d.Name,d.Enabled)).ToDictionaryAsync(x=>x.Id,ct);
        if(apiId is Guid api&&!apis.ContainsKey(api)||appId is Guid app&&!apps.ContainsKey(app)||destinationId is Guid destination&&!destinations.ContainsKey(destination))throw ScopeResolver.Missing();
        var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(x=>environments.Contains(x.EnvironmentId)&&x.Enabled).Select(x=>new ExpectedObservationNode(x.EnvironmentId,x.NodeName)).ToArrayAsync(ct);
        var workingIds=await db.Set<Policy>().AsNoTracking().Where(p=>p.OrganizationId==scope.OrganizationId&&(p.ProjectId==null||p.ProjectId==project.Id)).Select(p=>p.Id).ToArrayAsync(ct);
        var policyIds=environments.ToDictionary(env=>env,_=>new HashSet<Guid>(workingIds));
        // Query only historical policy identifiers; never load credentials or complete snapshots for observation scope.
        if(environments.Length>0) {
            var history=await db.Database.SqlQueryRaw<HistoricalPolicyObservationIdentity>("""
                SELECT v.environment_id AS "EnvironmentId", COALESCE(p->>'sourcePolicyId',p->>'id') AS "PolicyId"
                FROM gateway_config_versions v JOIN gateway_config_snapshots s ON s.config_version_id=v.id
                CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(s.payload->'policies')='array' THEN s.payload->'policies' ELSE '[]'::jsonb END) p
                WHERE v.environment_id=ANY(@environments)
                """,new NpgsqlParameter("environments",environments)).ToArrayAsync(ct);
            foreach(var row in history) if(Guid.TryParse(row.PolicyId,out var id)) policyIds[row.EnvironmentId].Add(id);
        }
        return new(scope.OrganizationId,scope.ProjectId,environments,nodes,apis,apps,destinations,policyIds.ToDictionary(x=>x.Key,x=>(IReadOnlySet<Guid>)x.Value));
    }
}

public sealed class HistoricalPolicyObservationIdentity {public Guid EnvironmentId {get;set;}public string? PolicyId {get;set;}}

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
    public async Task<Guid[]> ResolveEnvironmentIdsAsync(ActorContext actor,string permission,ObservationScopeRequest scope,CancellationToken ct)
    {
        var project=await db.Set<Project>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==scope.ProjectId&&x.OrganizationId==scope.OrganizationId,ct)??throw ScopeResolver.Missing();
        Guid[] environments;
        if(scope.AllAccessibleEnvironments)
        {
            var projectScope = new ScopeRef(scope.OrganizationId, project.Id);
            await new PasswordLifecycleGuard(db).RequireBusinessAccessAsync(actor, ct);
            if (!await authorization.FunctionalPermissions(actor, projectScope).AnyAsync(p => p.Code == permission, ct))
                throw new ApiException(403, "permission_denied", "缺少操作权限。");
            if (project.Status != "Active" || !await db.Set<Organization>().AnyAsync(o => o.Id == scope.OrganizationId && o.Status == "Active", ct))
                throw new ApiException(409, "inactive_scope", "组织或项目已停用。");
            var read = permission.EndsWith(".read", StringComparison.Ordinal);
            var grants = db.Set<UserProjectScope>().Where(g => g.UserId == actor.UserId && g.OrganizationId == scope.OrganizationId
                && (g.ProjectId == null || g.ProjectId == project.Id) && (read || g.AccessMode == "read_write"));
            environments = await db.Set<EnvironmentRecord>().Where(e => e.ProjectId == project.Id && e.Status == "Active"
                    && grants.Any(g => g.EnvironmentId == null || g.EnvironmentId == e.Id))
                .OrderBy(e => e.Id).Select(e => e.Id).ToArrayAsync(ct);
            if (environments.Length == 0 && !await grants.AnyAsync(g => g.EnvironmentId == null, ct)) throw ScopeResolver.Missing();
        }
        else
        {
            if(scope.EnvironmentId is not Guid environment||!await db.Set<EnvironmentRecord>().AnyAsync(x=>x.Id==environment&&x.ProjectId==project.Id,ct))throw ScopeResolver.Missing();
            try{await authorization.RequireAsync(actor,permission,new("observation",environment,new(scope.OrganizationId,scope.ProjectId,environment)),ct);}
            catch(ApiException e)when(e.Code=="scope_denied"){throw ScopeResolver.Missing();}
            environments=[environment];
        }
        return environments;
    }
    public async Task<TrustedObservationScope> ResolveAsync(ActorContext actor,string permission,ObservationScopeRequest scope,Guid? apiId,Guid? appId,Guid? destinationId,CancellationToken ct,DateTimeOffset? destinationHealthAt=null,bool includeDestinationNames=false,DateTimeOffset? destinationWindowStart=null)
    {
        var environments=await ResolveEnvironmentIdsAsync(actor,permission,scope,ct);
        var apis=await db.Set<Api>().AsNoTracking().Where(x=>x.OrganizationId==scope.OrganizationId&&x.ProjectId==scope.ProjectId).ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);
        var apps = await ObservationApplicationQuery.ReadAsync(db, scope.OrganizationId, scope.ProjectId, environments, ct);
        if (appId is Guid applicationId)
        {
            if (!apps.TryGetValue(applicationId, out var app)) throw ScopeResolver.Missing();
            if (app.SharedEnvironmentIds is { } allowed) environments = environments.Where(allowed.Contains).ToArray();
            apps = new Dictionary<Guid, ObservationApplication> { [applicationId] = app };
        }
        var membership = await AppliedDestinationQuery.ReadAsync(db, scope.OrganizationId, scope.ProjectId, environments, destinationHealthAt, ct, includeDestinationNames, destinationWindowStart);
        if(apiId is Guid api&&!apis.ContainsKey(api)||destinationId is Guid destination&&!membership.Destinations.ContainsKey(destination))throw ScopeResolver.Missing();
        var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(x=>environments.Contains(x.EnvironmentId)&&x.Enabled).Select(x=>new ExpectedObservationNode(x.EnvironmentId,x.NodeName)).ToArrayAsync(ct);
        var workingIds=await db.Set<Policy>().AsNoTracking().Where(p=>p.OrganizationId==scope.OrganizationId&&(p.ProjectId==null||p.ProjectId==scope.ProjectId)).Select(p=>p.Id).ToArrayAsync(ct);
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
        return new TrustedObservationScope(scope.OrganizationId,scope.ProjectId,environments,nodes,apis,apps,membership.Destinations,
            policyIds.ToDictionary(x=>x.Key,x=>(IReadOnlySet<Guid>)x.Value)) { Membership = membership };
    }
}

public sealed class HistoricalPolicyObservationIdentity {public Guid EnvironmentId {get;set;}public string? PolicyId {get;set;}}

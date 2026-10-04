using Microsoft.EntityFrameworkCore;
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
        var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(x=>environments.Contains(x.EnvironmentId)&&x.Enabled&&x.Status=="Active").Select(x=>new ExpectedObservationNode(x.EnvironmentId,x.NodeName)).ToArrayAsync(ct);
        return new(scope.OrganizationId,scope.ProjectId,environments,nodes,apis,apps,destinations);
    }
}

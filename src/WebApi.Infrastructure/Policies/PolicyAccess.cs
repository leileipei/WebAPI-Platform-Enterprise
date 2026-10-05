using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Policies;
public sealed class PolicyAccess(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes)
{
    public async Task<ScopeRef> ResolveAsync(PolicyScopeRequest scope,CancellationToken ct)
    {
        if(scope.ProjectId is Guid project) {var actual=await scopes.ProjectAsync(project,ct);if(actual.OrganizationId!=scope.OrganizationId) throw ScopeResolver.Missing();return actual;}
        if(!await db.Set<Organization>().AsNoTracking().AnyAsync(o=>o.Id==scope.OrganizationId,ct)) throw ScopeResolver.Missing();return new(scope.OrganizationId);
    }
    public async Task<bool> CanReadAsync(ScopeRef scope,ActorContext actor,CancellationToken ct)
    {
        if(await auth.CanAsync(actor,"policy.read",new("policy",Guid.Empty,scope),ct)) return true;
        // Organization policies are shared with authorized descendants; writes still use the owning scope.
        if(scope.ProjectId is not null) return false;
        var grants=await db.Set<UserProjectScope>().AsNoTracking().Where(g=>g.UserId==actor.UserId&&g.OrganizationId==scope.OrganizationId).ToArrayAsync(ct);
        foreach(var grant in grants) {
            ScopeRef descendant;
            try {descendant=grant.EnvironmentId is Guid env?await scopes.EnvironmentAsync(env,ct):grant.ProjectId is Guid project?await scopes.ProjectAsync(project,ct):scope;}
            catch(WebApi.Contracts.Common.ApiException e) when(e.Status==404) {continue;}
            if(descendant.OrganizationId==scope.OrganizationId&&await auth.CanAsync(actor,"policy.read",new("policy",Guid.Empty,descendant),ct)) return true;
        }
        return false;
    }
}

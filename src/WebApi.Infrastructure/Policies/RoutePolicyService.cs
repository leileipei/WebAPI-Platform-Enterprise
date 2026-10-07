using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Security;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Policies;
public sealed class RoutePolicyService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,PolicyAccess access,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,JwtApplicationBindingService mappings,GatewayPolicyDeploymentRules deployment)
{
    public static async Task<PolicyBindingConfiguration[]> LoadAsync(WebApiDbContext db,Guid routeId,CancellationToken ct)
    {return await (from b in db.Set<RoutePolicyBinding>() join p in db.Set<Policy>() on b.PolicyId equals p.Id where b.RouteId==routeId select new PolicyBindingConfiguration(p.Id,p.Type,p.Config,p.Enabled,b.Priority)).ToArrayAsync(ct);}
    public async Task<RoutePoliciesDto> GetAsync(Guid routeId,ActorContext actor,CancellationToken ct=default)
    {
        var r=await db.Set<ApiRoute>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==routeId,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(r.EnvironmentId,ct);
        if(!await auth.CanAsync(actor,"route.read",new("route",r.Id,scope),ct)) throw ScopeResolver.Missing();
        var rows=await (from b in db.Set<RoutePolicyBinding>().AsNoTracking() join p in db.Set<Policy>().AsNoTracking() on b.PolicyId equals p.Id where b.RouteId==routeId orderby b.Priority,p.Id select new {b.Priority,Policy=p}).ToArrayAsync(ct);
        var bindings=new List<PolicyBindingDto>();foreach(var row in rows) {if(!await access.CanReadAsync(new(row.Policy.OrganizationId,row.Policy.ProjectId),actor,ct)) throw ScopeResolver.Missing();bindings.Add(new(row.Policy.Id,row.Priority,PolicyService.Dto(row.Policy)));}
        return new(routeId,r.Revision,bindings);
    }
    public async Task<CommandResult<RoutePoliciesDto>> ReplaceAsync(Guid routeId,SaveRoutePoliciesRequest request,string? ifMatch,ActorContext actor,CancellationToken ct=default)
    {
        var original=await db.Set<ApiRoute>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==routeId,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(original.EnvironmentId,ct);
        if(!await auth.CanAsync(actor,"route.read",new("route",routeId,scope),ct)) throw ScopeResolver.Missing();
        return await commands.ExecuteAsync(actor,scope,"route.policies.replace",async(_,token)=>{
            await auth.RequireAsync(actor,"route.write",new("route",routeId,scope),token);await auth.RequireAsync(actor,"policy.write",new("route",routeId,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"route.policies.replace",requestContext.IdempotencyKey),CanonicalJson.Serialize(new {routeId,request,ifMatch}),async inner=>{
                var route=await db.Set<ApiRoute>().SingleAsync(r=>r.Id==routeId,inner);PolicyPreconditions.Require(ifMatch,route.Revision);
                if(request.Bindings.Count>6) throw new ApiException(422,"invalid_policy_binding","单路由最多六种策略。");
                var policies=new List<PolicyBindingDto>();foreach(var b in request.Bindings) {
                    var p=await db.Set<Policy>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==b.PolicyId,inner);
                    if(p is null||p.OrganizationId!=scope.OrganizationId||p.ProjectId is Guid project&&project!=scope.ProjectId||!await access.CanReadAsync(new(p.OrganizationId,p.ProjectId),actor,inner)) throw new ApiException(422,"invalid_policy_binding","策略不属于路由可绑定范围。");
                    deployment.Validate(p.Type,p.Config);
                    if(p.Type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(p.Config).Jwt is {} jwt) await mappings.ValidateAsync(new(p.OrganizationId,p.ProjectId),jwt,actor,inner);
                    policies.Add(new(p.Id,b.Priority,PolicyService.Dto(p)));
                }
                PolicyBindingRules.Validate(policies.Select(b=>new PolicyBindingConfiguration(b.PolicyId,b.Policy.Type,b.Policy.Config,b.Policy.Enabled,b.Priority)).ToArray(),true,route.TimeoutMs);
                var previous=await db.Set<RoutePolicyBinding>().Where(b=>b.RouteId==routeId).ToArrayAsync(inner);
                db.RemoveRange(previous.Where(b=>!request.Bindings.Any(n=>n.PolicyId==b.PolicyId)));
                foreach(var binding in request.Bindings) {var existing=previous.SingleOrDefault(b=>b.PolicyId==binding.PolicyId);if(existing is not null) existing.Priority=binding.Priority;else db.Add(new RoutePolicyBinding {RouteId=routeId,PolicyId=binding.PolicyId,Priority=binding.Priority});}
                route.Revision++;route.UpdatedAt=DateTimeOffset.UtcNow;
                return new CommandResult<RoutePoliciesDto>(new(routeId,route.Revision,policies.OrderBy(b=>b.Priority).ThenBy(b=>b.PolicyId).ToArray()),RevisionTag.Format(route.Revision));
            },token);
        },ct);
    }
}

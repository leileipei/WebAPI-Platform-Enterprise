using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Routing;

public sealed class RouteListReader(WebApiDbContext db)
{
    public async Task<IReadOnlyDictionary<Guid,EffectiveRoutePolicies>> ReadEffectiveAsync(IReadOnlyList<ApiRoute> routes,CancellationToken ct)
    {
        if(routes.Count==0)return new Dictionary<Guid,EffectiveRoutePolicies>();
        var ids=routes.Select(r=>r.Id).ToArray();
        var rows=await (from binding in db.Set<RoutePolicyBinding>().AsNoTracking()
            join policy in db.Set<Policy>().AsNoTracking() on binding.PolicyId equals policy.Id
            where ids.Contains(binding.RouteId)
            select new {binding.RouteId,policy.Id,policy.Type,policy.Config,policy.Enabled,binding.Priority}).ToArrayAsync(ct);
        // Include disabled and invalid bindings: the same domain validator must decide their effect.
        var bindings=rows.ToLookup(row=>row.RouteId,row=>new PolicyBindingConfiguration(row.Id,row.Type,row.Config,row.Enabled,row.Priority));
        return routes.ToDictionary(r=>r.Id,r=>PolicyBindingRules.Validate(bindings[r.Id].ToArray(),true,r.TimeoutMs));
    }
}

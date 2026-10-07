using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Security;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.Policies;

public sealed class JwtApplicationBindingService(WebApiDbContext db, AuthorizationService auth, PolicyReferenceService references)
{
    public async Task ValidateAsync(ScopeRef scope, JwtAuthenticationConfiguration config, ActorContext? actor, CancellationToken ct)
    {
        foreach (var id in config.ApplicationMappings.Select(m => m.ApplicationId).Distinct())
        {
            var app=await db.Set<ApplicationRecord>().AsNoTracking().SingleOrDefaultAsync(a=>a.Id==id,ct);
            if (app is null || app.OrganizationId != scope.OrganizationId
                || app.ProjectId is Guid project && (scope.ProjectId is null || project != scope.ProjectId)
                || actor is not null && !await auth.CanAsync(actor,"app.read",new("application",id,new(app.OrganizationId,app.ProjectId)),ct))
                throw new ApiException(422,"invalid_application_mapping","认证应用映射不属于可使用范围。");
        }
    }
    public static IReadOnlySet<Guid> ReferencedApplicationIds(string type, string config)
    {
        if (type != "authentication") return new HashSet<Guid>();
        var authentication=PolicyConfigurationValidator.ParseAuthentication(config);
        return authentication.Jwt?.ApplicationMappings.Select(m=>m.ApplicationId).ToHashSet() ?? [];
    }
    public async Task RequireNotReferencedAsync(Guid applicationId, CancellationToken ct)
    {
        var org=await db.Set<ApplicationRecord>().Where(a=>a.Id==applicationId).Select(a=>a.OrganizationId).SingleAsync(ct);
        foreach (var policy in await references.ProtectedConfigurationsAsync(org,ct))
            if (ReferencedApplicationIds(policy.Type,policy.Config).Contains(applicationId))
                throw new ApiException(409,"application_in_use","应用仍被认证策略或受保护的发布快照引用。");
    }
}

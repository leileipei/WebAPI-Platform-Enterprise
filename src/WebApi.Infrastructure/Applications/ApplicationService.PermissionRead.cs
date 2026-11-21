using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Applications;

public sealed partial class ApplicationService
{
    internal async Task<ResourceRef> RequireReadablePermissionProjectAsync(Guid applicationId, Guid projectId, ActorContext actor, CancellationToken ct)
    {
        var app = new ResourceRef("application", applicationId, await scopes.ApplicationAsync(applicationId, ct));
        if (!await auth.CanAsync(actor, "app.read", app, ct)) throw ScopeResolver.Missing();
        var project = await scopes.ProjectAsync(projectId, ct);
        if (app.Scope.OrganizationId != project.OrganizationId || app.Scope.ProjectId is Guid owner && owner != projectId ||
            !await auth.CanAsync(actor, "api.read", new("project", projectId, project), ct)) throw ScopeResolver.Missing();
        return app;
    }

    internal IQueryable<EnvironmentRecord> ReadablePermissionEnvironments(ActorContext actor)
    {
        var apis = visibility.Scopes(actor, "api.read");
        var environments = visibility.Scopes(actor, "environment.read");
        var app = visibility.Scopes(actor, "app.read");
        return db.Set<EnvironmentRecord>().AsNoTracking().Where(e => e.Status == "Active" &&
            apis.Any(s => s.ProjectId == e.ProjectId && s.EnvironmentId == null) &&
            environments.Any(s => s.EnvironmentId == e.Id) && app.Any(s => s.EnvironmentId == e.Id));
    }

    private async Task<(ApplicationPermissionDto[], Dictionary<Guid, long?>)> ReadVisiblePermissionsAsync(Guid id, ActorContext actor, CancellationToken ct)
    {
        var routeRead = visibility.Scopes(actor, "route.read");
        var configRead = visibility.Scopes(actor, "gateway.config.read");
        var environmentAccess = await ReadablePermissionEnvironments(actor)
            .Where(e => db.Set<ApplicationApiPermission>().Any(p => p.ApplicationId == id && p.EnvironmentId == e.Id))
            .Select(e => new { e.Id, Routes = routeRead.Any(s => s.EnvironmentId == e.Id), Config = configRead.Any(s => s.EnvironmentId == e.Id) })
            .ToArrayAsync(ct);
        var ids = environmentAccess.Select(e => e.Id).ToArray();
        var grants = await db.Set<ApplicationApiPermission>().AsNoTracking().Where(p => p.ApplicationId == id && ids.Contains(p.EnvironmentId))
            .ToListAsync(ct);
        var routeIds = environmentAccess.Where(e => e.Routes).Select(e => e.Id).ToHashSet();
        var complete = (await ReadPermissionDetailsAsync(grants.Where(p => routeIds.Contains(p.EnvironmentId)).ToList(), ct)).ToDictionary(p => p.Id);
        var permissions = grants.Select(p => complete.TryGetValue(p.Id, out var details) ? details :
            Dto(p) with { Mode = p.Mode == "RouteMethod" ? p.Mode : null }).ToArray();

        var configIds = environmentAccess.Where(e => e.Config).Select(e => e.Id).ToArray();
        var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(n => n.Enabled && configIds.Contains(n.EnvironmentId))
            .Select(n => new { n.EnvironmentId, n.CurrentConfigVersion, n.LastHeartbeatAt }).ToArrayAsync(ct);
        var byEnvironment = nodes.ToLookup(n => n.EnvironmentId);
        var threshold = DateTimeOffset.UtcNow.AddSeconds(-30);
        var versions = new Dictionary<Guid, long?>();
        foreach (var environmentId in configIds)
        {
            var cohort = byEnvironment[environmentId].ToArray();
            versions[environmentId] = cohort.Length > 0 && cohort.All(n => n.LastHeartbeatAt > threshold &&
                n.CurrentConfigVersion == cohort[0].CurrentConfigVersion) ? cohort[0].CurrentConfigVersion : null;
        }
        return (permissions, versions);
    }
}

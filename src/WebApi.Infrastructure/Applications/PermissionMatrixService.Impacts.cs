using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;

namespace WebApi.Infrastructure.Applications;

public sealed partial class PermissionMatrixService
{
    private sealed record RouteIdentity(string Path, long? Revision, bool Enabled, IReadOnlyList<string> Methods);

    private async Task<PreparedChange[]> PrepareAsync(Guid applicationId, MatrixChange[] changes,
        Dictionary<Guid, EnvironmentAccess> access, ActorContext actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var prepared = new List<PreparedChange>(changes.Length);
        foreach (var group in AffectedCells(changes).GroupBy(c => c.EnvironmentId))
        {
            var env = access[group.Key];
            var apiIds = group.Select(c => c.ApiId).ToArray();
            var parents = await db.Set<ApplicationApiPermission>().AsNoTracking().Where(g => g.ApplicationId == applicationId &&
                g.EnvironmentId == group.Key && apiIds.Contains(g.ApiId)).ToListAsync(ct);
            var byApi = (await applications.ReadPermissionDetailsAsync(parents, ct)).ToDictionary(p => p.ApiId);
            var routeIds = byApi.Values.SelectMany(g => g.RouteMethods ?? []).Concat(group.SelectMany(c => c.RouteMethods ?? []))
                .Select(g => g.RouteId).Distinct().ToArray();
            var paths = env.ReadRoutes && routeIds.Length > 0
                ? await db.Set<ApiRoute>().AsNoTracking().Where(r => r.EnvironmentId == group.Key && apiIds.Contains(r.ApiId) &&
                    routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => new RouteIdentity(r.Path, r.AuthorizationRevision, r.Enabled, r.Methods), ct) : [];
            var running = env.ReadRunning ? await ReadRunningAsync(group.Key, ct) : null;
            if (group.Any(c => c.Action != "Revoke"))
                await GlobalCredentialPublicationGuard.RequireApplicationInheritanceAsync(db, env.Resource.Scope, applicationId, ct);

            foreach (var change in group)
            {
                byApi.TryGetValue(change.ApiId, out var grant);
                if (change.PermissionId is Guid id)
                {
                    if (grant?.Id != id) throw new ApiException(412, "stale_revision", "授权已变化，请刷新后重试。");
                    RevisionTag.Require(RevisionTag.Format(change.Revision!.Value), grant.Revision);
                }
                else if (grant is not null)
                    throw new ApiException(409, "permission_already_exists", "该单元已经存在授权，请编辑现有记录。");

                var before = grant is null ? null : Summarize(grant.Mode, grant.ValidFrom, grant.ExpiresAt, grant.RouteMethods, env.ReadRoutes, paths, now);
                PermissionSaveRequest? save = null;
                MatrixGrantImpact? after = null;
                if (change.Action != "Revoke")
                {
                    var preserve = change.Action == "SetWindow";
                    var mode = preserve ? grant!.Mode ?? "LegacyApi" : change.Mode!;
                    var routes = preserve ? grant!.RouteMethods : change.RouteMethods;
                    if (mode == "RouteMethod")
                    {
                        await auth.RequireAsync(actor, "route.read", env.Resource, ct);
                        await applications.RequireCurrentRoutesAsync(change.ApiId, group.Key, ApplicationService.ValidateMethods(routes), ct);
                    }
                    else
                    {
                        if (grant?.Mode == "RouteMethod")
                            throw new ApiException(409, "explicit_permission_mode_required", "路由方法授权不能降级为旧版授权。");
                        await applications.RequireLegacyEnvironmentAsync([group.Key], ct);
                    }
                    save = new(change.ApiId, group.Key, change.ValidFrom!.Value, change.ExpiresAt, mode, routes);
                    after = Summarize(mode, save.ValidFrom, save.ExpiresAt, routes, env.ReadRoutes, paths, now);
                }
                prepared.Add(new(change, save, new(change.ApiId, group.Key, env.Environment.IsProduction, before, after,
                    RunningGrant(running, env, applicationId, change.ApiId, now))));
            }
        }
        if (changes is [{ Action: "MoveLegacy" } move])
        {
            if (prepared.Single(p => p.Save is null).Impact.Before?.Mode != "LegacyApi")
                throw new ApiException(409, "explicit_permission_mode_required", "只允许移动旧版 API 授权。");
            await applications.RequireLegacyEnvironmentAsync([move.EnvironmentId], ct);
        }
        return prepared.ToArray();
    }

    private async Task<RunningDeployment?> ReadRunningAsync(Guid environmentId, CancellationToken ct)
    {
        try { return await deployments.ReadAsync(environmentId, null, ct); }
        catch (ApiException e) when (e.Status is 404 or 409 or 422) { return null; }
    }

    private static MatrixRunning RunningGrant(RunningDeployment? deployment, EnvironmentAccess access, Guid appId, Guid apiId,
        DateTimeOffset now)
    {
        if (!access.ReadRunning) return new("NotVisible");
        if (deployment is null) return new("Unconfirmed");
        var snapshot = deployment.Snapshot.Snapshot;
        var parents = snapshot.Applications.SingleOrDefault(a => a.Id == appId)?.Permissions.Where(p => p.ApiId == apiId).Take(2).ToArray();
        // Legacy snapshots can contain multiple parents. The gateway rejects this ambiguous cell.
        if (parents is { Length: > 1 }) return new("Unconfirmed");
        var permission = parents?.SingleOrDefault();
        MatrixGrantImpact? grant = null;
        if (permission is not null)
        {
            var paths = access.ReadRoutes ? snapshot.Routes.Where(r => r.ApiId == apiId).ToDictionary(r => r.Id,
                r => new RouteIdentity(r.Path, r.AuthorizationRevision, true, r.Methods)) : [];
            grant = Summarize(permission.Mode, permission.ValidFrom, permission.ExpiresAt, permission.RouteMethods, access.ReadRoutes, paths, now);
        }
        return new("Confirmed", snapshot.ConfigVersion, deployment.Release.DeploymentSequence, grant);
    }

    private static MatrixGrantImpact Summarize(string? mode, DateTimeOffset from, DateTimeOffset? expires,
        IReadOnlyList<RuntimeRouteMethodGrant>? routes, bool readRoutes, Dictionary<Guid, RouteIdentity> paths, DateTimeOffset now) =>
        new(mode ?? "LegacyApi", Phase(from, expires, now), from, expires, routes?.Sum(r => r.Methods.Count) ?? 0,
            readRoutes ? routes?.OrderBy(r => r.RouteId).Select(r =>
            {
                var identity = paths.GetValueOrDefault(r.RouteId);
                var matches = identity is { Enabled: true } && identity.Revision == r.AuthorizationRevision &&
                    !r.Methods.Except(identity.Methods, StringComparer.Ordinal).Any();
                return new MatrixRoute(r.RouteId, matches ? identity!.Path : null, r.AuthorizationRevision,
                    r.Methods.Order(StringComparer.Ordinal).ToArray(), matches);
            }).ToArray() : null);
}

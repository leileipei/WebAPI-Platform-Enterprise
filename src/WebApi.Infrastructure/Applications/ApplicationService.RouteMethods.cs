using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Applications;

public sealed partial class ApplicationService
{
    public async Task<RouteMethodOptionsDto> ReadRouteMethodOptionsAsync(Guid applicationId, Guid environmentId, Guid apiId,
        ActorContext actor, CancellationToken ct = default)
    {
        var env = await scopes.EnvironmentAsync(environmentId, ct);
        await RequireReadablePermissionProjectAsync(applicationId, env.ProjectId!.Value, actor, ct);
        if (!await ReadablePermissionEnvironments(actor).AnyAsync(e => e.Id == environmentId, ct) ||
            !await db.Set<Api>().AnyAsync(a => a.Id == apiId && a.ProjectId == env.ProjectId && a.OrganizationId == env.OrganizationId, ct))
            throw ScopeResolver.Missing();
        await RequireRoutePermissionScopeAsync(applicationId, apiId, environmentId, env, actor, ct);
        var choices = await db.Set<ApiRoute>().AsNoTracking().Where(x =>
                x.EnvironmentId == environmentId && x.ApiId == apiId && x.Enabled)
            .OrderBy(x => x.Path).ThenBy(x => x.Id).Take(1001)
            .Select(x => new RouteMethodChoice(x.Id, x.Path, x.AuthorizationRevision, x.Methods)).ToArrayAsync(ct);
        if (choices.Length > 1000)
            throw new ApiException(409, "route_method_options_too_large", "本 API 的启用路由超过一次授权编辑的选择范围。");
        return new(environmentId, apiId, choices);
    }

    private async Task<CommandResult<ApplicationPermissionDto>> SaveRouteMethodPermissionAsync(Guid applicationId, Guid? permissionId,
        PermissionSaveRequest request, string? tag, ActorContext actor, Func<WebApiDbContext, CancellationToken, Task>? requireSession,
        CancellationToken ct)
    {
        var requested = ValidateMethods(request.RouteMethods);
        ValidateWindow(request.ValidFrom, request.ExpiresAt);
        var envScope = await scopes.EnvironmentAsync(request.EnvironmentId, ct);
        return await commands.ExecuteAsync(actor, envScope, "app.permission.route-method.save", async (_, token) =>
        {
            if (requireSession is not null) await requireSession(db, token);
            await RequireRoutePermissionScopeAsync(applicationId, request.ApiId, request.EnvironmentId, envScope, actor, token);
            var environment = await db.Set<EnvironmentRecord>().FromSqlInterpolated(
                $"SELECT * FROM environments WHERE id={request.EnvironmentId} FOR UPDATE").SingleAsync(token);
            await GlobalCredentialPublicationGuard.RequireApplicationInheritanceAsync(db, envScope, applicationId, token);

            await RequireCurrentRoutesAsync(request.ApiId, environment.Id, requested, token);

            ApplicationApiPermission permission;
            if (permissionId is Guid id)
            {
                permission = await db.Set<ApplicationApiPermission>().SingleOrDefaultAsync(x => x.Id == id && x.ApplicationId == applicationId, token)
                    ?? throw ScopeResolver.Missing();
                if (permission.ApiId != request.ApiId || permission.EnvironmentId != environment.Id)
                    throw new ApiException(409, "route_method_permission_scope_changed", "路由方法授权的 API 和环境必须保持一致。");
                RevisionTag.Require(tag, permission.Revision);
                permission.Revision = checked(permission.Revision + 1);
            }
            else
            {
                if (await db.Set<ApplicationApiPermission>().AnyAsync(x => x.ApplicationId == applicationId &&
                        x.ApiId == request.ApiId && x.EnvironmentId == environment.Id, token))
                    throw new ApiException(409, "permission_already_exists", "该应用已有同一 API 和环境的授权，请编辑现有记录。");
                permission = new ApplicationApiPermission { ApplicationId = applicationId, ApiId = request.ApiId,
                    EnvironmentId = environment.Id, CreatedBy = actor.UserId };
                db.Add(permission);
            }

            permission.Mode = "RouteMethod";
            permission.ValidFrom = request.ValidFrom.ToUniversalTime();
            permission.ExpiresAt = request.ExpiresAt?.ToUniversalTime();
            var rows = AsGrantRows(permission, requested.Values);
            var existing = await db.Set<ApplicationRouteMethodGrant>().Where(x => x.PermissionId == permission.Id).ToArrayAsync(token);
            foreach (var row in existing)
            {
                if (rows.Remove((row.RouteId, row.Method), out var approved))
                    row.AuthorizationRevision = approved.AuthorizationRevision;
                else db.Remove(row);
            }
            db.AddRange(rows.Values);

            var app = await db.Set<ApplicationRecord>().SingleAsync(x => x.Id == applicationId, token);
            app.Revision = checked(app.Revision + 1);
            app.AuthorizationRevision = checked(app.AuthorizationRevision + 1);
            app.UpdatedAt = DateTimeOffset.UtcNow;
            environment.AuthorizationRevision = checked(environment.AuthorizationRevision + 1);
            var dto = Dto(permission) with { Mode = permission.Mode, RouteMethods = requested.Values.OrderBy(x => x.RouteId).ToArray() };
            return new CommandResult<ApplicationPermissionDto>(dto, RevisionTag.Format(permission.Revision));
        }, ct);
    }

    internal async Task RequireCurrentRoutesAsync(Guid apiId, Guid environmentId, Dictionary<Guid, RuntimeRouteMethodGrant> requested, CancellationToken ct)
    {
        var routeIds = requested.Keys.ToArray();
        var routes = await db.Set<ApiRoute>().AsNoTracking().Where(x => routeIds.Contains(x.Id) && x.ApiId == apiId &&
            x.EnvironmentId == environmentId && x.Enabled).ToDictionaryAsync(x => x.Id, ct);
        foreach (var grant in requested.Values)
        {
            if (!routes.TryGetValue(grant.RouteId, out var route)) throw InvalidRouteMethods();
            RevisionTag.Require(RevisionTag.Format(grant.AuthorizationRevision), route.AuthorizationRevision);
            if (grant.Methods.Except(route.Methods, StringComparer.Ordinal).Any()) throw InvalidRouteMethods();
        }
    }

    private async Task RequireRoutePermissionScopeAsync(Guid applicationId, Guid apiId, Guid environmentId, ScopeRef env,
        ActorContext actor, CancellationToken ct)
    {
        var app = await scopes.ApplicationAsync(applicationId, ct);
        await auth.RequireAsync(actor, "app.permission.manage", new("application", applicationId, app), ct);
        await auth.RequireAsync(actor, "app.permission.manage", new("environment", environmentId, env), ct);
        var api = await scopes.ApiAsync(apiId, ct);
        if (api.OrganizationId != env.OrganizationId || api.OrganizationId != app.OrganizationId ||
            api.ProjectId != env.ProjectId || app.ProjectId is Guid project && project != api.ProjectId)
            throw new ApiException(422, "foreign_authorization", "应用、API 与环境范围不一致。");
        await auth.RequireAsync(actor, "route.read", new("environment", environmentId, env), ct);
    }

    internal async Task<ApplicationPermissionDto[]> ReadPermissionDetailsAsync(List<ApplicationApiPermission> permissions, CancellationToken ct)
    {
        var governed = permissions.Where(x => x.Mode == "RouteMethod").Select(x => x.Id).ToArray();
        if (governed.Length == 0) return permissions.Select(Dto).ToArray();
        var grants = await db.Set<ApplicationRouteMethodGrant>().AsNoTracking().Where(x => governed.Contains(x.PermissionId)).ToArrayAsync(ct);
        var byPermission = grants.ToLookup(x => x.PermissionId);
        return permissions.Select(permission => permission.Mode == "RouteMethod"
            ? Dto(permission) with { Mode = permission.Mode, RouteMethods = byPermission[permission.Id].GroupBy(x => (x.RouteId, x.AuthorizationRevision))
                .OrderBy(x => x.Key.RouteId).ThenBy(x => x.Key.AuthorizationRevision)
                .Select(x => new RuntimeRouteMethodGrant(x.Key.RouteId, x.Key.AuthorizationRevision,
                    x.Select(method => method.Method).Order(StringComparer.Ordinal).ToArray())).ToArray() }
            : Dto(permission)).ToArray();
    }

    internal static Dictionary<Guid, RuntimeRouteMethodGrant> ValidateMethods(IReadOnlyList<RuntimeRouteMethodGrant>? grants)
    {
        if (grants is not { Count: >= 1 and <= 1000 }) throw InvalidRouteMethods();
        var result = new Dictionary<Guid, RuntimeRouteMethodGrant>();
        foreach (var grant in grants)
        {
            if (grant is null || grant.RouteId == Guid.Empty || grant.AuthorizationRevision <= 0 ||
                grant.Methods is not { Count: >= 1 and <= 7 } || grant.Methods.Any(method =>
                    method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS")) ||
                grant.Methods.Distinct(StringComparer.Ordinal).Count() != grant.Methods.Count ||
                !result.TryAdd(grant.RouteId, grant with { Methods = grant.Methods.Order(StringComparer.Ordinal).ToArray() }))
                throw InvalidRouteMethods();
        }
        return result;
    }

    private static Dictionary<(Guid RouteId, string Method), ApplicationRouteMethodGrant> AsGrantRows(
        ApplicationApiPermission permission, IEnumerable<RuntimeRouteMethodGrant> approved) =>
        approved.SelectMany(grant => grant.Methods.Select(method => new ApplicationRouteMethodGrant
        {
            PermissionId = permission.Id, ApiId = permission.ApiId, EnvironmentId = permission.EnvironmentId,
            RouteId = grant.RouteId, Method = method, AuthorizationRevision = grant.AuthorizationRevision
        })).ToDictionary(x => (x.RouteId, x.Method));

    private static ApiException InvalidRouteMethods() =>
        new(422, "invalid_route_method_permissions", "须提供当前环境和 API 下不重复的路由修订及适用 HTTP 方法。");
}

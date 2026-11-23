using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;

namespace WebApi.Infrastructure.Delivery;

internal sealed class RetainedRouteAccess
{
    private readonly HashSet<Guid> wholeApis;
    private readonly HashSet<Guid> apiKeyApis;
    private readonly HashSet<Guid> apiKeyRoutes;
    private readonly ILookup<Guid, Guid> jwtApis;
    private readonly HashSet<(Guid ApplicationId, Guid RouteId)> jwtRoutes;

    internal RetainedRouteAccess(RuntimeSnapshot snapshot, IReadOnlyList<RuntimeRoute> retained, IReadOnlySet<Guid>? mappedApis = null)
    {
        wholeApis = mappedApis is null ? [] : retained.Select(route => route.ApiId).Except(mappedApis).ToHashSet();
        var keyRoutes = retained.Where(route => route.RequireApiKey).ToArray();
        apiKeyApis = keyRoutes.Select(route => route.ApiId).ToHashSet();
        apiKeyRoutes = keyRoutes.Select(route => route.Id).ToHashSet();
        var applications = snapshot.Policies.Where(policy => policy.Type == "authentication").ToDictionary(policy => policy.Id,
            policy => PolicyConfigurationValidator.ParseAuthentication(policy.Config).Jwt?.ApplicationMappings ?? []);
        var jwt = (from route in retained where route.AuthenticationMode == AuthenticationMode.JWT
                   from binding in route.PolicyBindings ?? [] where applications.ContainsKey(binding.PolicyId)
                   from mapping in applications[binding.PolicyId] select (mapping.ApplicationId, route.ApiId, RouteId: route.Id)).ToArray();
        jwtApis = jwt.ToLookup(row => row.ApplicationId, row => row.ApiId);
        jwtRoutes = jwt.Select(row => (row.ApplicationId, row.RouteId)).ToHashSet();
    }

    private bool NeedsKey(RuntimePermission grant) => grant.Mode == "RouteMethod"
        ? (grant.RouteMethods ?? []).Any(method => apiKeyRoutes.Contains(method.RouteId))
        : apiKeyApis.Contains(grant.ApiId);

    internal bool NeedsCredentials(RuntimeApplication app) => app.Permissions.Any(grant => wholeApis.Contains(grant.ApiId) || NeedsKey(grant)) ||
        jwtApis[app.Id].Any(wholeApis.Contains);

    internal IEnumerable<RuntimePermission> PermissionsFor(RuntimeApplication app) => app.Permissions.Where(grant =>
        wholeApis.Contains(grant.ApiId) || NeedsKey(grant) || (grant.Mode == "RouteMethod"
            ? (grant.RouteMethods ?? []).Any(method => jwtRoutes.Contains((app.Id, method.RouteId)))
            : jwtApis[app.Id].Contains(grant.ApiId)));

    internal Guid[] ApiIdsFor(RuntimeApplication app) => PermissionsFor(app).Select(grant => grant.ApiId)
        .Concat(jwtApis[app.Id]).Distinct().Order().ToArray();

    internal void RequireUnchanged(RuntimeSnapshot baseline, RuntimeSnapshot current)
    {
        if (!CanonicalJson.Serialize(Access(baseline)).AsSpan().SequenceEqual(CanonicalJson.Serialize(Access(current))))
            throw Changed();
    }

    private RuntimeApplication[] Access(RuntimeSnapshot snapshot) => snapshot.Applications.Where(app => ApiIdsFor(app).Length > 0)
        .OrderBy(app => app.Id).Select(app => app with
        {
            Credentials = NeedsCredentials(app) ? app.Credentials.OrderBy(key => key.Id).ToArray() : [],
            Permissions = PermissionsFor(app).OrderBy(grant => grant.ApiId).Select(grant => grant with
            {
                RouteMethods = grant.RouteMethods?.Where(method => wholeApis.Contains(grant.ApiId) || apiKeyRoutes.Contains(method.RouteId) ||
                    jwtRoutes.Contains((app.Id, method.RouteId))).OrderBy(method => method.RouteId).ToArray()
            }).ToArray()
        }).ToArray();

    internal static ApiException Changed() => new(409, "promotion_retained_access_changed",
        "所选凭据或授权会改变同一 API 下保留路由的访问范围，请先对该变更单独审批。");
}

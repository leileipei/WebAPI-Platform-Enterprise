using WebApi.Contracts.Runtime;

namespace WebApi.Domain.Applications;

public static class PermissionEvaluator
{
    private static readonly HashSet<string> Methods = new(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], StringComparer.Ordinal);

    public static bool IsAllowed(RuntimeApplication app, RuntimeRoute route, string method, DateTimeOffset now)
    {
        if (app.Status != "Active" || !Methods.Contains(method) || !route.Methods.Contains(method, StringComparer.Ordinal)) return false;
        var parents = app.Permissions.Where(permission => permission.ApiId == route.ApiId).Take(2).ToArray();
        if (parents.Length != 1) return false;
        var permission = parents[0];
        if (now < permission.ValidFrom || permission.ExpiresAt is { } expires && now >= expires) return false;
        if (permission.Mode is null or "LegacyApi")
            return (permission.RouteMethods is null or { Count: 0 }) &&
                (permission.EnvironmentId is null || permission.EnvironmentId == route.EnvironmentId && app.EnvironmentId == route.EnvironmentId);
        if (permission.Mode != "RouteMethod" || route.AuthorizationRevision is not > 0 || route.EnvironmentId is null || route.EnvironmentId == Guid.Empty) return false;
        if (app.EnvironmentId != route.EnvironmentId || permission.EnvironmentId != route.EnvironmentId) return false;
        return permission.RouteMethods?.Any(grant => grant.RouteId == route.Id && grant.AuthorizationRevision == route.AuthorizationRevision &&
            grant.Methods.Contains(method, StringComparer.Ordinal)) == true;
    }
}

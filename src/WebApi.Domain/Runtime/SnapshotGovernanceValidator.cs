using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;

namespace WebApi.Domain.Runtime;

internal static class SnapshotGovernanceValidator
{
    private static readonly string[] RootFields =
    [
        "schemaVersion", "environmentId", "configVersion", "generatedAt", "routes", "clusters", "policies", "applications",
        "requiredSecurityGeneration", "requiredSecurityEpoch", "requireEnvironmentCredentials", "minimumSnapshotSchema"
    ];
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    private static ApiException Invalid() => new(422, "invalid_snapshot", "治理快照字段或安全身份不合法。");

    public static void ValidateWire(JsonElement root)
    {
        Object(root, RootFields, RootFields);
        foreach (var route in Elements(root, "routes"))
        {
            Object(route, ["id", "apiId", "apiVersionId", "clusterId", "path", "methods", "matchOrder", "timeoutMs", "requireApiKey", "policyBindings", "authenticationMode", "authorizationRevision", "environmentId"],
                ["id", "apiId", "apiVersionId", "clusterId", "path", "methods", "matchOrder", "timeoutMs", "requireApiKey", "authorizationRevision", "environmentId"]);
            if (route.TryGetProperty("policyBindings", out _))
                foreach (var binding in Elements(route, "policyBindings")) Object(binding, ["policyId", "priority"], ["policyId", "priority"]);
        }
        foreach (var cluster in Elements(root, "clusters"))
        {
            Object(cluster, ["id", "environmentId", "loadBalancingPolicy", "healthCheckEnabled", "healthCheckPath", "healthCheckIntervalSec", "destinations", "sourceId"],
                ["id", "environmentId", "loadBalancingPolicy", "healthCheckEnabled", "healthCheckPath", "healthCheckIntervalSec", "destinations"]);
            foreach (var destination in Elements(cluster, "destinations")) Object(destination, ["id", "address", "weight"], ["id", "address", "weight"]);
        }
        foreach (var policy in Elements(root, "policies"))
            Object(policy, ["id", "type", "config", "sourcePolicyId", "sourceRevision"], ["id", "type", "config"]);
        foreach (var app in Elements(root, "applications"))
        {
            Object(app, ["id", "status", "credentials", "permissions", "environmentId"], ["id", "status", "credentials", "permissions", "environmentId"]);
            foreach (var credential in Elements(app, "credentials"))
                Object(credential, ["id", "accessKey", "hash", "status", "validFrom", "expiresAt", "environmentId", "bindingMode"],
                    ["id", "accessKey", "hash", "status", "validFrom", "expiresAt", "environmentId", "bindingMode"]);
            foreach (var permission in Elements(app, "permissions"))
            {
                Object(permission, ["apiId", "validFrom", "expiresAt", "mode", "routeMethods", "environmentId"],
                    ["apiId", "validFrom", "expiresAt", "mode", "routeMethods", "environmentId"]);
                foreach (var grant in Elements(permission, "routeMethods"))
                    Object(grant, ["routeId", "authorizationRevision", "methods"], ["routeId", "authorizationRevision", "methods"]);
            }
        }
    }

    public static void Validate(RuntimeSnapshot snapshot)
    {
        if (snapshot.RequiredSecurityGeneration is null || snapshot.RequiredSecurityGeneration == Guid.Empty ||
            snapshot.RequiredSecurityEpoch is null or < 0 || snapshot.RequireEnvironmentCredentials is null ||
            snapshot.MinimumSnapshotSchema is not ("2.0" or "2.1" or "2.2" or "2.3" or "2.4")) throw Invalid();
        if (snapshot.SchemaVersion == "2.3" && snapshot.MinimumSnapshotSchema == "2.4") throw Invalid();
        if (snapshot.Routes is null || snapshot.Applications is null) throw Invalid();
        var routes = new Dictionary<Guid, RuntimeRoute>();
        foreach (var route in snapshot.Routes)
            if (route is null || route.Methods is null || route.EnvironmentId != snapshot.EnvironmentId ||
                route.AuthorizationRevision is not > 0 || !routes.TryAdd(route.Id, route)) throw Invalid();

        var credentialIds = new HashSet<Guid>();
        foreach (var app in snapshot.Applications)
        {
            if (app is null || app.Id == Guid.Empty || app.EnvironmentId != snapshot.EnvironmentId ||
                app.Credentials is null || app.Permissions is null || app.Permissions.Count > 10000 ||
                app.Permissions.Any(permission => permission is null) ||
                app.Permissions.Select(permission => permission.ApiId).Distinct().Count() != app.Permissions.Count) throw Invalid();
            foreach (var credential in app.Credentials)
                if (credential is null || credential.Id == Guid.Empty || !credentialIds.Add(credential.Id) || credential.BindingMode is not ("LegacyGlobal" or "EnvironmentBound") ||
                    credential.BindingMode == "EnvironmentBound" && credential.EnvironmentId != snapshot.EnvironmentId ||
                    credential.BindingMode == "LegacyGlobal" && (credential.EnvironmentId is not null || snapshot.RequireEnvironmentCredentials == true)) throw Invalid();
            foreach (var permission in app.Permissions)
            {
                if (permission.EnvironmentId != snapshot.EnvironmentId || permission.Mode is not ("LegacyApi" or "RouteMethod") ||
                    permission.RouteMethods is null || permission.RouteMethods.Count > 4096 ||
                    permission.Mode == "LegacyApi" && (permission.RouteMethods.Count != 0 || snapshot.MinimumSnapshotSchema is "2.3" or "2.4")) throw Invalid();
                var ids = new HashSet<Guid>();
                foreach (var grant in permission.RouteMethods)
                {
                    if (grant is null || !ids.Add(grant.RouteId) || grant.AuthorizationRevision <= 0 ||
                        !routes.TryGetValue(grant.RouteId, out var route) || route.ApiId != permission.ApiId ||
                        grant.Methods is null || grant.Methods.Count is < 1 or > 7 ||
                        grant.Methods.Distinct(StringComparer.Ordinal).Count() != grant.Methods.Count ||
                        grant.Methods.Any(method => !Methods.Contains(method, StringComparer.Ordinal))) throw Invalid();
                    if (grant.AuthorizationRevision == route.AuthorizationRevision &&
                        grant.Methods.Any(method => !route.Methods.Contains(method, StringComparer.Ordinal))) throw Invalid();
                }
            }
        }
    }

    private static void Object(JsonElement value, string[] allowed, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            names.Any(name => !allowed.Contains(name, StringComparer.Ordinal)) ||
            required.Any(name => !value.TryGetProperty(name, out _))) throw Invalid();
    }

    private static IEnumerable<JsonElement> Elements(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) throw Invalid();
        foreach (var item in array.EnumerateArray()) yield return item;
    }
}

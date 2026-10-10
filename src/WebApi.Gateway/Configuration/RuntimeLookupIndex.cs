using System.Collections.Frozen;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;

namespace WebApi.Gateway.Configuration;

public sealed record RuntimeCredentialEntry(RuntimeApplication Application, RuntimeCredential Credential);
public sealed record RuntimeAuthenticationEntry(AuthenticationConfiguration? Configuration, RuntimePolicy? Policy);

public sealed class RuntimeLookupIndex
{
    public IReadOnlyDictionary<Guid, RuntimeRoute> Routes { get; }
    public IReadOnlyDictionary<Guid, RuntimeCluster> Clusters { get; }
    public IReadOnlyDictionary<Guid, RuntimeApplication> Applications { get; }
    public IReadOnlyDictionary<string, RuntimeCredentialEntry> Credentials { get; }
    public IReadOnlyDictionary<Guid, RuntimeAuthenticationEntry> RouteAuthentication { get; }
    public IReadOnlyDictionary<(Guid PolicyId, string ClaimValue), Guid> JwtApplications { get; }
    public IReadOnlyDictionary<(Guid ApplicationId, Guid ApiId), IReadOnlyList<RuntimePermission>> PermissionWindows { get; }

    private RuntimeLookupIndex(RuntimeSnapshot frozen, IReadOnlyDictionary<Guid, AuthenticationConfiguration> authentication)
    {
        Routes = Unique(frozen.Routes, route => route.Id);
        Clusters = Unique(frozen.Clusters, cluster => cluster.Id);
        Applications = Unique(frozen.Applications, application => application.Id);
        var policies = Unique(frozen.Policies, policy => policy.Id);
        if (authentication.Any(pair => pair.Value is null || !policies.TryGetValue(pair.Key, out var policy) || policy.Type != "authentication")
            || policies.Values.Any(policy => policy.Type == "authentication" && !authentication.ContainsKey(policy.Id)))
            throw new InvalidOperationException("Authentication configuration must match the generation policies.");

        var routeAuthentication = new Dictionary<Guid, RuntimeAuthenticationEntry>();
        foreach (var route in frozen.Routes)
        {
            if (!Clusters.ContainsKey(route.ClusterId)) throw new InvalidOperationException("Route cluster is missing.");
            RuntimeAuthenticationEntry entry = new(null, null);
            foreach (var binding in route.PolicyBindings ?? [])
            {
                if (!policies.TryGetValue(binding.PolicyId, out var policy)) throw new InvalidOperationException("Bound policy is missing.");
                if (!authentication.TryGetValue(binding.PolicyId, out var configuration)) continue;
                if (entry.Configuration is not null) throw new InvalidOperationException("Route has more than one authentication binding.");
                entry = new(configuration, policy);
            }
            routeAuthentication.Add(route.Id, entry);
        }
        RouteAuthentication = routeAuthentication.ToFrozenDictionary();
        Credentials = Unique(frozen.Applications.SelectMany(application => application.Credentials.Select(credential => new RuntimeCredentialEntry(application, credential))),
            entry => entry.Credential.AccessKey, StringComparer.Ordinal);

        var jwtApplications = new Dictionary<(Guid PolicyId, string ClaimValue), Guid>();
        foreach (var (policyId, configuration) in authentication)
        {
            if (configuration.Mode != AuthenticationMode.JWT) continue;
            var jwt = configuration.Jwt ?? throw new InvalidOperationException("JWT configuration is missing.");
            foreach (var mapping in jwt.ApplicationMappings)
            {
                if (!Applications.ContainsKey(mapping.ApplicationId)) throw new InvalidOperationException("JWT application is missing.");
                // Tuple string equality is ordinal; the runtime policy ID is the namespace.
                if (!jwtApplications.TryAdd((policyId, mapping.ClaimValue), mapping.ApplicationId))
                    throw new InvalidOperationException("JWT mapping key is duplicated.");
            }
        }
        JwtApplications = jwtApplications.ToFrozenDictionary();

        var apiIds = frozen.Routes.Select(route => route.ApiId).ToHashSet();
        var windows = new Dictionary<(Guid ApplicationId, Guid ApiId), IReadOnlyList<RuntimePermission>>();
        foreach (var application in frozen.Applications)
        {
            foreach (var group in application.Permissions.GroupBy(permission => permission.ApiId))
            {
                if (!apiIds.Contains(group.Key)) throw new InvalidOperationException("Granted API is missing.");
                // Keep expired, future and overlapping windows; admission evaluates time.
                windows.Add((application.Id, group.Key), Array.AsReadOnly(group.ToArray()));
            }
        }
        PermissionWindows = windows.ToFrozenDictionary();
    }

    public static RuntimeLookupIndex Build(RuntimeSnapshot frozen, IReadOnlyDictionary<Guid, AuthenticationConfiguration> authentication) => new(frozen, authentication);

    private static FrozenDictionary<TKey, TValue> Unique<TKey, TValue>(IEnumerable<TValue> values, Func<TValue, TKey> key, IEqualityComparer<TKey>? comparer = null) where TKey : notnull
    {
        var dictionary = new Dictionary<TKey, TValue>(comparer);
        foreach (var value in values)
            if (!dictionary.TryAdd(key(value), value)) throw new InvalidOperationException("Runtime lookup key is duplicated.");
        return dictionary.ToFrozenDictionary(comparer);
    }
}

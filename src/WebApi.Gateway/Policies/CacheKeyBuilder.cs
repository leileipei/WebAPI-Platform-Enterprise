using System.Text;
using System.Text.Json;
using WebApi.Contracts.Policies;
namespace WebApi.Gateway.Policies;
public sealed record CacheLookupKey(string Opaque);
public sealed class CacheKeyBuilder(GatewayCacheSettings settings)
{
    public CacheLookupKey? Build(TrafficExecutionContext execution, HttpRequest request, CacheConfiguration configuration)
    {
        var identity = execution.VerifiedIdentity;
        if (identity is null) return null;
        var generation = execution.Generation;
        var policies = (execution.Route.PolicyBindings ?? []).Select(b => generation.PoliciesById[b.PolicyId]).ToArray();
        var authentication = policies.SingleOrDefault(p => p.Type == "authentication");
        var cache = policies.SingleOrDefault(p => p.Type == "cache");
        if (cache is null) return null;
        var auth = authentication is null ? null : generation.Authentication[authentication.Id];
        var partition = identity.Mode.ToString();
        if (identity.Mode != AuthenticationMode.Anonymous)
        {
            if (identity.ApplicationId is null) return null;
            partition += ":" + identity.ApplicationId.Value.ToString("N");
        }
        if (identity.Mode == AuthenticationMode.JWT)
        {
            if (identity.Jwt is not { } jwt) return null;
            var user = JsonSerializer.SerializeToUtf8Bytes(new[] { jwt.Issuer, jwt.Subject });
            partition += ":" + settings.Hash(user);
            if (auth?.Jwt?.ForwardBearer == true) partition += ":" + settings.Hash(Encoding.UTF8.GetBytes(jwt.ValidatedBearer));
        }
        var vary = configuration.VaryHeaders.Select(name => new { name = name.ToLowerInvariant(), values = request.Headers[name].ToArray() }).ToArray();
        var input = JsonSerializer.SerializeToUtf8Bytes(new { ns = settings.KeyPrefix, environment = generation.Snapshot.EnvironmentId, sequence = generation.Envelope.DeploymentSequence,
            route = execution.Route.Id, version = execution.Route.ApiVersionId, cluster = execution.Route.ClusterId, authentication = authentication?.Id, cache = cache.Id,
            partition, method = request.Method, path = request.PathBase.Value + request.Path.Value, query = request.QueryString.Value, host = request.Host.Value, vary });
        return input.Length > 16384 ? null : new(settings.Hash(input));
    }
}

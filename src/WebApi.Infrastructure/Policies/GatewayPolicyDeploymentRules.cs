using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;

namespace WebApi.Infrastructure.Policies;

public sealed class GatewayPolicyDeploymentRules
{
    private readonly HashSet<string> fixtureOrigins;
    private readonly HashSet<string> varyHeaders;
    public PolicyLimitsDto Limits { get; }
    public GatewayPolicyDeploymentRules(int maxEntryBytes = 1048576, IReadOnlyList<string>? allowedVaryHeaders = null, IReadOnlyList<string>? httpFixtureOrigins = null)
    {
        if (maxEntryBytes is < 1024 or > 1048576) throw new InvalidOperationException("Invalid gateway cache entry limit.");
        var headers = new[] { "Accept", "Accept-Language", "Accept-Encoding" }.Concat(allowedVaryHeaders ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Validate deployment extensions with the same sensitive-header/token rules as policy input.
        foreach (var header in headers)
            PolicyConfigurationValidator.Normalize("cache", System.Text.Json.JsonSerializer.Serialize(new { ttlSeconds=60, maxEntryBytes, varyHeaders=new[]{header}, identityPartition="VerifiedIdentity", redisFailureMode="Bypass" }));
        if (headers.Length > 64) throw new InvalidOperationException("Invalid gateway vary header limit.");
        fixtureOrigins = new(StringComparer.Ordinal);
        foreach (var origin in httpFixtureOrigins ?? [])
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.GetLeftPart(UriPartial.Authority) != origin)
                throw new InvalidOperationException("Invalid gateway JWT fixture origin.");
            fixtureOrigins.Add(origin);
        }
        varyHeaders = new(headers, StringComparer.OrdinalIgnoreCase);
        Limits = new(maxEntryBytes, Array.AsReadOnly(headers));
    }
    public static GatewayPolicyDeploymentRules Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var headers=configuration.GetSection("GatewayPolicies:Cache:AllowedVaryHeaders").Get<string[]>();
        var fixtures = environment.IsDevelopment() && configuration.GetValue<bool>("GatewayPolicies:FixtureEnabled")
            ? configuration.GetSection("GatewayPolicies:Jwt:HttpFixtureOrigins").Get<string[]>() : [];
        return new(configuration.GetValue<int?>("GatewayPolicies:Cache:MaxEntryBytes") ?? 1048576, headers, fixtures);
    }
    public void Validate(string type, string normalizedConfig)
    {
        if (type == "authentication")
        {
            var authentication=PolicyConfigurationValidator.ParseAuthentication(normalizedConfig);
            if (authentication.Jwt is {} jwt && new Uri(jwt.Issuer).Scheme == "http"
                && !fixtureOrigins.Contains(new Uri(jwt.Issuer).GetLeftPart(UriPartial.Authority))) throw Invalid();
        }
        else if (type == "cache")
        {
            var cache=PolicyConfigurationValidator.ParseCache(normalizedConfig);
            if (cache.MaxEntryBytes > Limits.MaxEntryBytes || cache.VaryHeaders.Any(h => !varyHeaders.Contains(h))) throw Invalid();
        }
    }
    private static ApiException Invalid() => new(422,"invalid_policy_config","策略配置超出当前部署允许范围。");
}

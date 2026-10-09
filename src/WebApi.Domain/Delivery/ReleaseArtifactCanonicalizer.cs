using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Domain.Routing;

namespace WebApi.Domain.Delivery;

public static class ReleaseArtifactCanonicalizer
{
    private static ApiException Invalid() => new(422, "invalid_artifact_content", "制品契约、路由或策略槽位不合法。");
    public static string Hash(ArtifactContent content) => Convert.ToHexStringLower(SHA256.HashData(Serialize(content)));
    public static byte[] Serialize(ArtifactContent content) => CanonicalJson.Serialize(Normalize(content));

    public static ArtifactContent Normalize(ArtifactContent content)
    {
        if (content.Apis.Count == 0 || content.Apis.Select(a => a.ApiId).Distinct().Count() != content.Apis.Count || content.Apis.Select(a => a.VersionId).Distinct().Count() != content.Apis.Count) throw Invalid();
        var apis = content.Apis.OrderBy(a => a.ApiId).ThenBy(a => a.VersionId).Select(a =>
        {
            if (a.ApiId == Guid.Empty || a.VersionId == Guid.Empty || a.SourceRevision < 1 || a.Parameters.Any(p => p.ApiVersionId != a.VersionId) || a.Schemas.Any(s => s.ApiVersionId != a.VersionId)) throw Invalid();
            return a with
            {
                Parameters = a.Parameters.OrderBy(p => p.Location, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id).Select(p => p with { Schema = Json(p.Schema), ExampleJson = Json(p.ExampleJson) }).ToArray(),
                Schemas = a.Schemas.OrderBy(s => s.SchemaType, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.StatusCode).ThenBy(s => s.ContentType, StringComparer.Ordinal).ThenBy(s => s.Id).Select(s => s with { SchemaJson = Json(s.SchemaJson)!, SchemaHash = null, ExampleJson = Json(s.ExampleJson) }).ToArray()
            };
        }).ToArray();
        var routes = content.Routes.Select(r =>
        {
            if (!apis.Any(a => a.ApiId == r.ApiId && a.VersionId == r.VersionId) || r.AuthenticationMode is not ("ApiKey" or "Anonymous" or "JWT") || r.TimeoutTemplate is not null) throw Invalid();
            var path = RouteNormalizer.Normalize(r.Path);
            var methods = r.Methods.Select(m => m.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (methods.Length == 0 || methods.Any(m => m.Length > 16 || m.Length == 0 || m.Any(c => c is < 'A' or > 'Z'))) throw Invalid();
            RouteNormalizer.MatchOrder(r.Path, r.Priority);
            var key = Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(new { r.ApiId, r.VersionId, path, methods })));
            if (r.Key.Length > 0 && r.Key != key) throw Invalid();
            var policies = r.Policies.Select(ArtifactPolicyTemplates.Normalize).OrderBy(p => p.Priority).ThenBy(p => p.Type, StringComparer.Ordinal).ToArray();
            if (policies.Select(p => p.Type).Distinct(StringComparer.Ordinal).Count() != policies.Length) throw Invalid();
            var authentication = policies.SingleOrDefault(p => p.Type == "authentication");
            if (authentication is not null)
            {
                using var mode = JsonDocument.Parse(authentication.FrozenConfig);
                if (mode.RootElement.GetProperty("mode").GetString() != r.AuthenticationMode) throw Invalid();
            }
            else if (r.AuthenticationMode == "JWT") throw Invalid();
            return r with { Key = key, Methods = methods, Policies = policies };
        }).OrderBy(r => r.Key, StringComparer.Ordinal).ToArray();
        if (routes.Length == 0 || routes.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != routes.Length) throw Invalid();
        return new(apis, routes);
    }

    private static string? Json(string? value)
    {
        if (value is null) return null;
        try { using var doc = JsonDocument.Parse(value); return Encoding.UTF8.GetString(CanonicalJson.Serialize(doc.RootElement)); }
        catch (JsonException) { throw Invalid(); }
    }
}

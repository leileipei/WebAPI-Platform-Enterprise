using System.Globalization;
using System.Text.Json;
using WebApi.Contracts.Policies;
using static WebApi.Domain.Policies.PolicyConfigurationValidator;

namespace WebApi.Domain.Policies;

public static class CorsPolicyConfiguration
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
        { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
    private static readonly HashSet<string> ProtectedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Set-Cookie", "Forwarded", "Connection", "Keep-Alive", "Proxy-Connection",
        "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "traceparent", "tracestate", "baggage"
    };

    internal static CorsConfiguration Parse(JsonElement root)
    {
        Fields(Names(root), "allowedOrigins", "allowCredentials", "allowedMethods", "allowedHeaders", "exposedHeaders", "maxAgeSeconds");
        var origins = StringArray(root, "allowedOrigins", 1, 64, 512)
            .Select(v => v == "*" ? v : NormalizeOrigin(v)).ToArray();
        if (origins.Distinct(StringComparer.Ordinal).Count() != origins.Length) throw Invalid();
        var credentials = Boolean(root, "allowCredentials");
        if (origins.Contains("*") && (origins.Length != 1 || credentials)) throw Invalid();
        var methods = StringArray(root, "allowedMethods", 1, 7, 7).Select(v => v.ToUpperInvariant()).ToArray();
        if (methods.Any(v => !Methods.Contains(v)) || methods.Distinct(StringComparer.Ordinal).Count() != methods.Length) throw Invalid();
        return new(origins, credentials, methods, Headers(root, "allowedHeaders", 64),
            Headers(root, "exposedHeaders", 32), Integer(root, "maxAgeSeconds", 0, 86400));
    }

    public static string NormalizeOrigin(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) throw Invalid();
        var delimiter = value.IndexOf("://", StringComparison.Ordinal);
        if (delimiter < 0) throw Invalid();
        var authority = value[(delimiter + 3)..];
        if (authority.Length == 0 || authority.IndexOfAny(['/', '?', '#', '@', '*']) >= 0 || authority.EndsWith(':')
            || !uri.IsWellFormedOriginalString()) throw Invalid();
        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6) host = "[" + host.Trim('[', ']') + "]";
        return uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
    }

    public static bool IsTokenChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
        || "!#$%&'*+-.^_`|~".Contains(c);
    internal static bool IsProtectedHeader(string name) => ProtectedHeaders.Contains(name)
        || name.StartsWith("X-WebApi-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Headers(JsonElement root, string field, int max)
    {
        var names = StringArray(root, field, 0, max, 128, StringComparer.OrdinalIgnoreCase);
        if (names.Any(name => name == "*" || !name.All(IsTokenChar) || IsProtectedHeader(name))) throw Invalid();
        return names;
    }
}

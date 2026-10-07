using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Security;
namespace WebApi.Gateway.Policies;
public sealed record CacheEligibilityResult(bool Eligible, string Reason);
public sealed record CacheStorageRule(bool Store, TimeSpan FreshFor, double InitialAge, string Reason);
public static class CacheEligibility
{
    internal static readonly IReadOnlySet<string> StoredHeaderNames = new HashSet<string>(new[] { "Content-Type", "Content-Encoding", "Content-Language", "ETag", "Last-Modified", "Cache-Control", "Expires", "Date", "Vary" }, StringComparer.OrdinalIgnoreCase);
    public static CacheEligibilityResult Request(HttpContext context, VerifiedTrafficIdentity identity, CacheConfiguration config, GatewayCacheSettings settings)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != false
            || request.ContentLength is >0 || request.Headers.ContainsKey("Transfer-Encoding")) return new(false, "unsafe_request");
        if (identity.Mode != AuthenticationMode.Anonymous && identity.ApplicationId is null) return new(false, "unverified_identity");
        if (identity.Mode == AuthenticationMode.JWT && identity.Jwt is null) return new(false, "unverified_identity");
        if (request.Headers.ContainsKey("Cookie") || identity.Mode != AuthenticationMode.JWT && request.Headers.ContainsKey("Authorization")) return new(false, "business_identity");
        foreach (var name in new[] { "Upgrade", "Range", "If-Match", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since", "If-Range" }) if (request.Headers.ContainsKey(name)) return new(false, "conditional_or_streaming");
        if (request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true || request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)) return new(false, "streaming");
        if (request.Headers.Connection.ToString().Split(',').Any(v => v.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase))) return new(false, "streaming");
        if (request.Headers.ContainsKey("Cache-Control"))
        {
            if (!StrictControl(request.Headers.CacheControl.ToString(), out var control) || control!.NoStore || control.NoCache || control.MaxAge == TimeSpan.Zero) return new(false, "fresh_source_required");
        }
        if (request.Headers.Pragma.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase)) return new(false, "fresh_source_required");
        var total = 0;
        foreach (var name in config.VaryHeaders)
        {
            if (!settings.AllowsVary(name)) return new(false, "unsupported_vary");
            var values = request.Headers[name];
            if (values.Count > 8) return new(false, "vary_budget");
            foreach (var value in values) { var bytes = Encoding.UTF8.GetByteCount(value ?? ""); if (bytes > 1024) return new(false, "vary_budget"); total += bytes; }
        }
        return total > 8192 ? new(false, "vary_budget") : new(true, "eligible");
    }
    public static CacheStorageRule Response(IHeaderDictionary headers, int status, DateTimeOffset requestAt, DateTimeOffset responseAt, CacheConfiguration config)
    {
        CacheStorageRule Bypass(string reason) => new(false, TimeSpan.Zero, 0, reason);
        if (status != 200 || headers.ContainsKey("Set-Cookie") || headers.ContainsKey("Trailer") || headers.ContainsKey("Content-Range")) return Bypass("response_ineligible");
        if (headers.ContentType.ToString().StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase)) return Bypass("streaming");
        if (headers.ContainsKey("Content-Encoding") && !headers.ContentEncoding.ToString().Equals("identity", StringComparison.OrdinalIgnoreCase)) return Bypass("unsupported_encoding");
        if (!StrictControl(headers.CacheControl.ToString(), out var control) || control!.NoStore || control.Private || control.NoCache) return Bypass("response_cache_control");
        var lifetime = control.SharedMaxAge ?? (control.Public ? control.MaxAge : null);
        if (lifetime is null || lifetime <= TimeSpan.Zero) return Bypass("no_shared_freshness");
        foreach (var name in headers.Vary.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (name == "*" || !config.VaryHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)) return Bypass("unsupported_vary");
        var sourceDate = responseAt;
        if (headers.ContainsKey("Date") && (headers.Date.Count != 1 || !DateTimeOffset.TryParseExact(headers.Date.ToString(), "R", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out sourceDate))) return Bypass("invalid_age");
        long age = 0;
        if (headers.ContainsKey("Age") && (headers.Age.Count != 1 || !long.TryParse(headers.Age.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out age) || age < 0)) return Bypass("invalid_age");
        var initial = Math.Max(Math.Max(0, (responseAt - sourceDate).TotalSeconds), age + Math.Max(0, (responseAt - requestAt).TotalSeconds));
        var fresh = Math.Min(config.TtlSeconds, lifetime.Value.TotalSeconds - initial);
        return fresh > 0 && double.IsFinite(initial) ? new(true, TimeSpan.FromSeconds(fresh), initial, "eligible") : Bypass("expired");
    }
    private static bool StrictControl(string text, out CacheControlHeaderValue? control)
    {
        control = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directive in text.Split(','))
        {
            var name = directive.Split('=', 2)[0].Trim();
            if (name.Length == 0 || !seen.Add(name)) return false;
        }
        return CacheControlHeaderValue.TryParse(text, out control);
    }
}

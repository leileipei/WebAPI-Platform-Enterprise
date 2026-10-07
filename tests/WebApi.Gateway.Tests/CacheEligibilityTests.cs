using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Security;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class CacheEligibilityTests
{
    internal static CacheConfiguration Config => new(60, 262144, ["Accept", "Accept-Language", "Accept-Encoding"], "VerifiedIdentity", "Bypass");
    internal sealed class SettingsFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "cache-test-" + Guid.NewGuid());
        internal string Prefix { get; } = "test:cache:" + Guid.NewGuid().ToString("N");
        internal string Secret { get; }
        internal GatewayCacheSettings Settings { get; }
        internal SettingsFixture()
        {
            Directory.CreateDirectory(directory); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Secret = Path.Combine(directory, "hmac.secret"); File.WriteAllText(Secret, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Settings = GatewayCacheSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["GatewayPolicies:Cache:HmacSecretFile"] = Secret, ["GatewayPolicies:Cache:KeyPrefix"] = Prefix, ["Redis:Connection"] = "redis:6379" }).Build());
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
    private sealed class Body(bool canHaveBody) : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => canHaveBody; }
    internal static DefaultHttpContext Request()
    { var c = new DefaultHttpContext(); c.Request.Method = "GET"; c.Request.Scheme = "https"; c.Request.Host = new("client.example"); c.Request.Path = "/orders"; c.Features.Set<IHttpRequestBodyDetectionFeature>(new Body(false)); return c; }
    internal static IHeaderDictionary Headers() => new HeaderDictionary { ["Cache-Control"] = "public,max-age=60", ["Content-Type"] = "application/json", ["Date"] = DateTimeOffset.FromUnixTimeSeconds(1800000000).ToString("R") };
    [Fact]
    public void LegacyBusinessAuthorizationAndCookiesBypass()
    {
        using var f = new SettingsFixture(); var c = Request(); var identity = new VerifiedTrafficIdentity(AuthenticationMode.ApiKey, Guid.NewGuid(), null);
        Assert.True(CacheEligibility.Request(c, identity, Config, f.Settings).Eligible);
        foreach (var header in new[] { "Authorization", "Cookie", "Range", "If-None-Match", "Upgrade" })
        { c = Request(); c.Request.Headers[header] = "synthetic"; Assert.False(CacheEligibility.Request(c, identity, Config, f.Settings).Eligible); }
        c = Request(); c.Request.Headers.Authorization = "Bearer verified"; Assert.True(CacheEligibility.Request(c, new(AuthenticationMode.JWT, Guid.NewGuid(), new("issuer", "sub", "app", "verified")), Config, f.Settings).Eligible);
        foreach (var method in new[] { "HEAD", "POST", "PUT" }) { c = Request(); c.Request.Method = method; Assert.False(CacheEligibility.Request(c, identity, Config, f.Settings).Eligible); }
        c = Request(); c.Features.Set<IHttpRequestBodyDetectionFeature>(new Body(true)); Assert.False(CacheEligibility.Request(c, identity, Config, f.Settings).Eligible);
        foreach (var control in new[] { "no-store", "no-cache", "max-age=0", "max-age=\"0\"", "invalid=\"" }) { c = Request(); c.Request.Headers.CacheControl = control; Assert.False(CacheEligibility.Request(c, identity, Config, f.Settings).Eligible); }
        c = Request(); c.Request.Headers.Accept = "text/event-stream"; Assert.False(CacheEligibility.Request(c, identity, Config, f.Settings).Eligible);
        c = Request(); c.Request.Headers.Accept = new string('界', 400); Assert.False(CacheEligibility.Request(c, identity, Config, f.Settings).Eligible);
    }
    [Fact]
    public void NoStorePrivateNoCacheUnknownVaryAndSetCookieNeverStore()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000); var h = Headers(); Assert.True(CacheEligibility.Response(h, 200, now, now, Config).Store);
        foreach (var value in new[] { "no-store,public,max-age=60", "private,max-age=60", "public,no-cache,max-age=60", "public", "max-age=60", "public,max-age=60,max-age=10", "public,max-age=invalid", "public,max-age=-1", "public,s-maxage=0" })
        { h = Headers(); h.CacheControl = value; Assert.False(CacheEligibility.Response(h, 200, now, now, Config).Store); }
        foreach (var pair in new[] { ("Set-Cookie", "session=secret"), ("Vary", "*"), ("Vary", "X-Unregistered"), ("Trailer", "X-Trailer"), ("Content-Type", "text/event-stream"), ("Content-Encoding", "gzip"), ("Age", "invalid"), ("Date", "invalid") })
        { h = Headers(); h[pair.Item1] = pair.Item2; Assert.False(CacheEligibility.Response(h, 200, now, now, Config).Store); }
        h = Headers(); h.Vary = "Accept,accept-language"; Assert.True(CacheEligibility.Response(h, 200, now, now, Config).Store);
        foreach (var status in new[] { 206, 304, 404, 503 }) Assert.False(CacheEligibility.Response(Headers(), status, now, now, Config).Store);
    }
    [Fact]
    public void AgeAndOriginLifetimeCapPolicyTtl()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000); var h = Headers(); h.CacheControl = "public,max-age=30"; h.Date = now.AddSeconds(-20).ToString("R"); h.Age = "10";
        var result = CacheEligibility.Response(h, 200, now.AddSeconds(-5), now, Config); Assert.True(result.Store); Assert.Equal(20, result.InitialAge); Assert.Equal(TimeSpan.FromSeconds(10), result.FreshFor);
        h.CacheControl = "s-maxage=120,max-age=30"; h.Age = "0"; h.Date = now.ToString("R"); result = CacheEligibility.Response(h, 200, now, now, Config); Assert.True(result.Store); Assert.Equal(TimeSpan.FromSeconds(60), result.FreshFor);
        h.CacheControl = "public,max-age=30"; h.Age = "30"; Assert.False(CacheEligibility.Response(h, 200, now, now, Config).Store);
    }
}

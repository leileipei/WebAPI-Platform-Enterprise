using System.Text.Json;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Security;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class CacheKeyTests
{
    private static readonly Guid App = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Env = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Route = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Cache = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Auth = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Cluster = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Api = Guid.Parse("77777777-7777-7777-7777-777777777777");
    internal static TrafficExecutionContext Execution(VerifiedTrafficIdentity identity, bool forward = false, long sequence = 1)
    {
        using var key = new JwtTokenVerifierTests.KeyFixture(); var jwt = key.Configuration() with { ForwardBearer = forward };
        var config = identity.Mode == AuthenticationMode.JWT ? JsonSerializer.Serialize(jwt, new JsonSerializerOptions(JsonSerializerDefaults.Web)) : JsonSerializer.Serialize(new { mode = identity.Mode });
        var policies = new[] { new RuntimePolicy(Auth, "authentication", PolicyConfigurationValidator.Normalize("authentication", config), Auth, 1), new RuntimePolicy(Cache, "cache", JsonSerializer.Serialize(CacheEligibilityTests.Config, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Cache, 1) };
        var route = new RuntimeRoute(Route, Api, Api, Cluster, "/orders", ["GET"], 0, 30000, identity.Mode == AuthenticationMode.ApiKey, [new(Auth, 0), new(Cache, 1)], identity.Mode);
        var snapshot = new RuntimeSnapshot("2.2", Env, 1, DateTimeOffset.UtcNow, [route], [], policies, []);
        var generation = new RuntimeGeneration(new(Guid.NewGuid(), sequence, 1, new string('a', 64), 0), [], snapshot);
        return new(generation, route) { ApplicationId = identity.ApplicationId, VerifiedIdentity = identity };
    }
    [Fact]
    public void IdentityAndTokenForwardingPartitionPrecisely()
    {
        using var f = new CacheEligibilityTests.SettingsFixture(); var builder = new CacheKeyBuilder(f.Settings); var request = CacheEligibilityTests.Request().Request;
        var a = new VerifiedTrafficIdentity(AuthenticationMode.JWT, App, new("https://issuer", "private-sub-A", "app", "token-A"));
        var b = new VerifiedTrafficIdentity(AuthenticationMode.JWT, App, new("https://issuer", "private-sub-B", "app", "token-A"));
        var refresh = new VerifiedTrafficIdentity(AuthenticationMode.JWT, App, new("https://issuer", "private-sub-A", "app", "token-B"));
        var first = builder.Build(Execution(a), request, CacheEligibilityTests.Config); Assert.NotNull(first);
        Assert.NotEqual(first, builder.Build(Execution(b), request, CacheEligibilityTests.Config)); Assert.Equal(first, builder.Build(Execution(refresh), request, CacheEligibilityTests.Config));
        Assert.NotEqual(builder.Build(Execution(a, true), request, CacheEligibilityTests.Config), builder.Build(Execution(refresh, true), request, CacheEligibilityTests.Config));
        Assert.NotEqual(first, builder.Build(Execution(a, sequence: 2), request, CacheEligibilityTests.Config));
        Assert.DoesNotContain("private-sub", JsonSerializer.Serialize(first)); Assert.DoesNotContain("token-A", JsonSerializer.Serialize(first)); Assert.DoesNotContain("https://issuer", JsonSerializer.Serialize(first));
        var apiKey = new VerifiedTrafficIdentity(AuthenticationMode.ApiKey, App, null); Assert.Equal(builder.Build(Execution(apiKey), request, CacheEligibilityTests.Config), builder.Build(Execution(apiKey), request, CacheEligibilityTests.Config));
        Assert.NotEqual(builder.Build(Execution(apiKey), request, CacheEligibilityTests.Config), builder.Build(Execution(new(AuthenticationMode.ApiKey, Guid.NewGuid(), null)), request, CacheEligibilityTests.Config));
    }
    [Fact]
    public void AllRawQueryValuesAndVaryAreIncluded()
    {
        using var f = new CacheEligibilityTests.SettingsFixture(); var builder = new CacheKeyBuilder(f.Settings); var c = CacheEligibilityTests.Request(); var execution = Execution(new(AuthenticationMode.Anonymous, null, null));
        c.Request.QueryString = new("?a=1&a=2&unknown=first"); c.Request.Headers.AcceptLanguage = "zh-CN"; var baseline = builder.Build(execution, c.Request, CacheEligibilityTests.Config); Assert.NotNull(baseline);
        foreach (var query in new[] { "?a=2&a=1&unknown=first", "?a=1&a=2&unknown=second", "?a=1&a=2&unknown=%66irst" }) { c.Request.QueryString = new(query); Assert.NotEqual(baseline, builder.Build(execution, c.Request, CacheEligibilityTests.Config)); }
        c.Request.QueryString = new("?a=1&a=2&unknown=first"); c.Request.Headers.AcceptLanguage = "en-US"; Assert.NotEqual(baseline, builder.Build(execution, c.Request, CacheEligibilityTests.Config));
        c.Request.Headers.AcceptLanguage = "zh-CN"; c.Request.QueryString = new("?padding=" + new string('x', 16384)); Assert.Null(builder.Build(execution, c.Request, CacheEligibilityTests.Config));
    }
}

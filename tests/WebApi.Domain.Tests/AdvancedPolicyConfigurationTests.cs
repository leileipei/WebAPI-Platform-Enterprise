using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class AdvancedPolicyConfigurationTests
{
    public const string Retry = """{"maxAttempts":2,"perAttemptTimeoutMs":3000,"baseDelayMs":50,"maxDelayMs":500,"jitterPercent":20,"retryStatusCodes":[502,503,504],"retryConnectionFailures":true}""";
    public const string Cache = """{"ttlSeconds":60,"maxEntryBytes":262144,"varyHeaders":["Accept","Accept-Language","Accept-Encoding"],"identityPartition":"VerifiedIdentity","redisFailureMode":"Bypass"}""";
    private static readonly Guid App = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static JsonObject PublicRsa(int bytes = 256)
    {
        var modulus = new byte[bytes]; modulus[0] = 128; modulus[^1] = 1;
        return new() { ["kid"] = "rsa-1", ["kty"] = "RSA", ["alg"] = "RS256", ["use"] = "sig", ["n"] = B64(modulus), ["e"] = "AQAB" };
    }
    private static JsonObject PublicEc()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        return new() { ["kid"] = "ec-1", ["kty"] = "EC", ["alg"] = "ES256", ["use"] = "sig", ["crv"] = "P-256", ["x"] = B64(q.X!), ["y"] = B64(q.Y!) };
    }
    private static JsonObject Jwt() => new()
    {
        ["mode"] = "JWT", ["issuer"] = "https://issuer.example/realm", ["audiences"] = new JsonArray("orders-api"),
        ["allowedAlgorithms"] = new JsonArray("RS256"), ["allowedTokenTypes"] = new JsonArray("JWT", "at+jwt"),
        ["clockSkewSeconds"] = 30, ["maxTokenLifetimeSeconds"] = 3600, ["applicationClaim"] = "azp",
        ["applicationMappings"] = new JsonArray(new JsonObject { ["claimValue"] = "orders-client", ["applicationId"] = App.ToString() }),
        ["jwks"] = new JsonObject { ["keys"] = new JsonArray(PublicRsa()) }, ["forwardBearer"] = false
    };
    private static void Reject(string type, string json) => Assert.Equal(422, Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize(type, json)).Status);
    private static void RejectJwt(Action<JsonObject> edit) { var source = Jwt(); edit(source); Reject("authentication", source.ToJsonString()); }
    private static void Accept(string type, JsonNode source)
    {
        var normalized = PolicyConfigurationValidator.Normalize(type, source.ToJsonString());
        Assert.Equal(normalized, PolicyConfigurationValidator.Normalize(type, normalized));
    }

    [Fact]
    public void JwtRejectsPrivateUnknownDuplicateAndMismatchedKeys()
    {
        Accept("authentication", Jwt());
        foreach (var forbidden in new[] { "d", "p", "q", "dp", "dq", "qi", "oth", "k", "jku", "x5u", "crit", "unknown" })
            RejectJwt(j => ((JsonObject)j["jwks"]!["keys"]![0]!)[forbidden] = "secret-canary");
        foreach (var algorithm in new[] { "none", "HS256", "RS384", "es256" }) RejectJwt(j => j["allowedAlgorithms"] = new JsonArray(algorithm));
        RejectJwt(j => j["jwks"]!["keys"] = new JsonArray(PublicRsa(), PublicRsa()));
        RejectJwt(j => j["jwks"]!["extra"] = true);
        RejectJwt(j => j["jwks"]!["keys"]![0]!["kty"] = "oct");
        RejectJwt(j => j["jwks"]!["keys"]![0]!["alg"] = "ES256");
        RejectJwt(j => j["jwks"]!["keys"]![0]!["use"] = "enc");
        RejectJwt(j => j["jwks"]!["keys"]![0]!["key_ops"] = new JsonArray("sign"));
        var text = Jwt().ToJsonString();
        Reject("authentication", text.Replace("\"kid\":\"rsa-1\"", "\"kid\":\"rsa-1\",\"kid\":\"rsa-2\""));
        Reject("authentication", text.Replace("\"applicationId\":", "\"claimValue\":\"other\",\"applicationId\":"));
        Reject("authentication", text.Replace("\"keys\":", "\"keys\":[],\"keys\":"));
    }

    [Fact]
    public void PublicKeySizesMetadataAndJwksBudgetsAreBounded()
    {
        foreach (var size in new[] { 256, 384, 512 }) { var j = Jwt(); j["jwks"]!["keys"] = new JsonArray(PublicRsa(size)); Accept("authentication", j); }
        foreach (var size in new[] { 255, 513 }) RejectJwt(j => j["jwks"]!["keys"] = new JsonArray(PublicRsa(size)));
        foreach (var field in new[] { "n", "e", "kid", "alg", "use" }) RejectJwt(j => ((JsonObject)j["jwks"]!["keys"]![0]!).Remove(field));
        foreach (var modulus in new[] { "", "AQ==", "not+base64", "_w", "A" }) RejectJwt(j => j["jwks"]!["keys"]![0]!["n"] = modulus);
        foreach (var exponent in new[] { "", "AA", "AQ", "Ag", "AQAB=", "AAEAAQ" }) RejectJwt(j => j["jwks"]!["keys"]![0]!["e"] = exponent);
        var ec = Jwt(); ec["allowedAlgorithms"] = new JsonArray("ES256"); ec["jwks"]!["keys"] = new JsonArray(PublicEc()); Accept("authentication", ec);
        foreach (var pair in new[] { ("crv", "P-384"), ("x", "AA"), ("y", "AA"), ("alg", "RS256") })
        { var invalid = ec.DeepClone(); invalid["jwks"]!["keys"]![0]![pair.Item1] = pair.Item2; Reject("authentication", invalid.ToJsonString()); }
        var meta = Jwt(); var key = (JsonObject)meta["jwks"]!["keys"]![0]!;
        key["key_ops"] = new JsonArray("verify"); key["x5t"] = "AQID"; key["x5t#S256"] = "AQID"; key["x5c"] = new JsonArray("AQID"); Accept("authentication", meta);
        foreach (var count in new[] { 0, 9 }) RejectJwt(j => j["jwks"]!["keys"] = Keys(count));
        var eight = Jwt(); eight["jwks"]!["keys"] = Keys(8); Accept("authentication", eight);
        RejectJwt(j => j["jwks"]!["keys"]![0]!["kid"] = new string('x', 257));
        RejectJwt(j => j["jwks"]!["keys"]![0]!["x5c"] = new JsonArray(new string('A', 33000)));
    }
    private static JsonArray Keys(int count)
    {
        var keys = new JsonArray(); for (var i = 0; i < count; i++) { var key = PublicRsa(); key["kid"] = "key-" + i; keys.Add(key); } return keys;
    }

    [Fact]
    public void JwtFieldsEnforceExactBoundsAndRejectReservedClaims()
    {
        foreach (var pair in new[] { ("clockSkewSeconds", -1), ("clockSkewSeconds", 121), ("maxTokenLifetimeSeconds", 59), ("maxTokenLifetimeSeconds", 86401) }) RejectJwt(j => j[pair.Item1] = pair.Item2);
        foreach (var pair in new[] { ("clockSkewSeconds", 0), ("clockSkewSeconds", 120), ("maxTokenLifetimeSeconds", 60), ("maxTokenLifetimeSeconds", 86400) }) { var j = Jwt(); j[pair.Item1] = pair.Item2; Accept("authentication", j); }
        foreach (var issuer in new[] { "", "ftp://issuer.example", "https://u:p@issuer.example", "https://issuer.example?x=1", "https://issuer.example#f", "/relative", "https://" + new string('x', 505) + ".example" }) RejectJwt(j => j["issuer"] = issuer);
        var http = Jwt(); http["issuer"] = "http://127.0.0.1:4900/realm"; Accept("authentication", http); // Deployment gate is Task 2.
        foreach (var count in new[] { 0, 9 }) RejectJwt(j => j["audiences"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode?)JsonValue.Create("a" + i)).ToArray()));
        var audience = Jwt(); audience["audiences"] = new JsonArray(Enumerable.Range(0, 8).Select(i => (JsonNode?)JsonValue.Create("a" + i)).ToArray()); Accept("authentication", audience);
        RejectJwt(j => j["audiences"] = new JsonArray("a", "a")); RejectJwt(j => j["audiences"] = new JsonArray("")); RejectJwt(j => j["audiences"] = new JsonArray(new string('a', 257)));
        foreach (var claim in new[] { "", "sub", "iss", "aud", "exp", "iat", "nbf", "jti", "$.azp", "a.b", "用户", new string('x', 65) }) RejectJwt(j => j["applicationClaim"] = claim);
        foreach (var typ in new[] { "", "jwt", "application/jwt" }) RejectJwt(j => j["allowedTokenTypes"] = new JsonArray(typ));
        RejectJwt(j => j["allowedTokenTypes"] = new JsonArray()); RejectJwt(j => j["allowedTokenTypes"] = new JsonArray("JWT", "JWT"));
        RejectJwt(j => j["allowedAlgorithms"] = new JsonArray()); RejectJwt(j => j["allowedAlgorithms"] = new JsonArray("RS256", "RS256"));
        foreach (var field in new[] { "issuer", "audiences", "allowedAlgorithms", "allowedTokenTypes", "applicationClaim", "applicationMappings", "jwks", "forwardBearer", "clockSkewSeconds", "maxTokenLifetimeSeconds" }) RejectJwt(j => j.Remove(field));
        RejectJwt(j => j["forwardBearer"] = "false"); RejectJwt(j => j["clockSkewSeconds"] = 0.5); RejectJwt(j => j["applicationMappings"]![0]!["applicationId"] = Guid.Empty.ToString());
        RejectJwt(j => j["applicationMappings"] = new JsonArray()); RejectJwt(j => j["applicationMappings"]!.AsArray().Add(j["applicationMappings"]![0]!.DeepClone()));
        RejectJwt(j => j["applicationMappings"]![0]!["extra"] = 1); RejectJwt(j => j["applicationMappings"]![0]!["claimValue"] = ""); RejectJwt(j => j["applicationMappings"]![0]!["claimValue"] = new string('x', 257));
        foreach (var count in new[] { 128, 129 })
        {
            var j = Jwt(); var maps = new JsonArray(); for (var i = 0; i < count; i++) maps.Add(new JsonObject { ["claimValue"] = "client-" + i, ["applicationId"] = App.ToString() }); j["applicationMappings"] = maps;
            if (count == 128) Accept("authentication", j); else Reject("authentication", j.ToJsonString());
        }
    }

    [Theory]
    [InlineData("retry", "maxAttempts", 1, 3)]
    [InlineData("retry", "perAttemptTimeoutMs", 10, 300000)]
    [InlineData("retry", "baseDelayMs", 0, 1000)]
    [InlineData("retry", "maxDelayMs", 0, 5000)]
    [InlineData("retry", "jitterPercent", 0, 100)]
    [InlineData("cache", "ttlSeconds", 1, 3600)]
    [InlineData("cache", "maxEntryBytes", 1024, 1048576)]
    public void RetryAndCacheEnforceExactBudgets(string type, string field, int min, int max)
    {
        foreach (var number in new[] { min - 1, min, max, max + 1 })
        {
            var j = JsonNode.Parse(type == "retry" ? Retry : Cache)!; j[field] = number;
            if (field == "baseDelayMs") j["maxDelayMs"] = 5000;
            if (field == "maxDelayMs") j["baseDelayMs"] = 0;
            if (number < min || number > max) Reject(type, j.ToJsonString()); else Accept(type, j);
        }
        foreach (var value in new[] { "1.5", "null", "true", "\"1\"", "NaN", "1e999" })
        {
            var text = type == "retry" ? Retry : Cache;
            var j = JsonNode.Parse(text)!; var original = j[field]!.ToJsonString();
            Reject(type, text.Replace("\"" + field + "\":" + original, "\"" + field + "\":" + value));
        }
    }

    [Fact]
    public void RetryAndCacheRejectUnknownDuplicatesAndUnsafeHeaders()
    {
        Accept("retry", JsonNode.Parse(Retry)!); Accept("cache", JsonNode.Parse(Cache)!);
        foreach (var source in new[] { ("retry", Retry), ("cache", Cache) })
        {
            Reject(source.Item1, source.Item2.Insert(1, "\"unknown\":true,"));
            var field = source.Item1 == "retry" ? "maxAttempts" : "ttlSeconds";
            Reject(source.Item1, source.Item2.Insert(1, "\"" + field + "\":1,"));
            foreach (var name in JsonNode.Parse(source.Item2)!.AsObject().Select(p => p.Key).ToArray())
            { var j = JsonNode.Parse(source.Item2)!; j.AsObject().Remove(name); Reject(source.Item1, j.ToJsonString()); }
        }
        foreach (var codes in new[] { new JsonArray(500), new JsonArray(502, 502), new JsonArray(502, 503, 504, 504), new JsonArray("502") })
        { var j = JsonNode.Parse(Retry)!; j["retryStatusCodes"] = codes; Reject("retry", j.ToJsonString()); }
        var disabled = JsonNode.Parse(Retry)!; disabled["retryStatusCodes"] = new JsonArray(); disabled["retryConnectionFailures"] = false; Reject("retry", disabled.ToJsonString()); disabled["maxAttempts"] = 1; Accept("retry", disabled);
        var delays = JsonNode.Parse(Retry)!; delays["baseDelayMs"] = 501; Reject("retry", delays.ToJsonString());
        foreach (var header in new[] { "Cookie", "Authorization", "X-API-Key", "Host", "X-WebApi-Deployment", "bad header", "", "é" })
        { var j = JsonNode.Parse(Cache)!; j["varyHeaders"] = new JsonArray(header); Reject("cache", j.ToJsonString()); }
        var cache = JsonNode.Parse(Cache)!; cache["varyHeaders"] = new JsonArray("Accept", "accept"); Reject("cache", cache.ToJsonString());
        cache["varyHeaders"] = new JsonArray(Enumerable.Range(0, 9).Select(i => (JsonNode?)JsonValue.Create("X-Business-" + i)).ToArray()); Reject("cache", cache.ToJsonString());
        cache["varyHeaders"] = new JsonArray(Enumerable.Range(0, 8).Select(i => (JsonNode?)JsonValue.Create("X-Business-" + i)).ToArray()); Accept("cache", cache);
        cache["varyHeaders"] = new JsonArray(); Accept("cache", cache);
        foreach (var pair in new[] { ("identityPartition", "None"), ("redisFailureMode", "Reject") }) { var j = JsonNode.Parse(Cache)!; j[pair.Item1] = pair.Item2; Reject("cache", j.ToJsonString()); }
    }

    [Fact]
    public void SixBindingsAllowJwtApplicationRateAndRejectAnonymousRate()
    {
        PolicyBindingConfiguration[] bindings = [Binding("authentication", Jwt().ToJsonString()), Binding("timeout", "{\"timeoutMs\":30000}"), Binding("rate_limit", PolicyConfigurationTests.Rate), Binding("circuit_breaker", PolicyConfigurationTests.Circuit), Binding("retry", Retry), Binding("cache", Cache)];
        var effective = PolicyBindingRules.Validate(bindings, true);
        Assert.False(effective.RequireApiKey); Assert.True(effective.HasApplicationIdentity); Assert.Equal(AuthenticationMode.JWT, effective.Authentication!.Mode);
        Assert.Equal(App, effective.Authentication.Jwt!.ApplicationMappings[0].ApplicationId);
        Assert.NotNull(effective.RateLimit); Assert.Equal(30000, effective.TimeoutMs);
        Assert.Equal(2, effective.Retry!.MaxAttempts); Assert.Equal(60, effective.Cache!.TtlSeconds);
        Assert.Throws<ApiException>(() => PolicyBindingRules.Validate(bindings.Where(b => b.Type != "timeout").ToArray(), true, 2999));
        Assert.NotNull(PolicyBindingRules.Validate(bindings.Where(b => b.Type != "timeout").ToArray(), true, 3000).Retry);
        Assert.Throws<ApiException>(() => PolicyBindingRules.Validate([.. bindings, Binding("cache", Cache)], true));
        Assert.Throws<ApiException>(() => PolicyBindingRules.Validate(bindings.Select(b => b.Type == "authentication" ? b with { Config = "{\"mode\":\"Anonymous\"}" } : b).ToArray(), true));
        Assert.Throws<ApiException>(() => PolicyBindingRules.Validate(bindings.Select(b => b.Type == "timeout" ? b with { Config = "{\"timeoutMs\":2999}" } : b).ToArray(), true));
        var disabledJwt = bindings.Select(b => b.Type == "authentication" ? b with { Enabled = false } : b).ToArray(); Assert.True(PolicyBindingRules.Validate(disabledJwt, true).RequireApiKey);
    }
    [Fact]
    public void TypedParsingRetainsExactIdentityValuesAndClonedJwks()
    {
        var source = Jwt(); source["issuer"] = "https://ISSUER.example/Realm"; source["applicationMappings"]![0]!["claimValue"] = "  exact client  ";
        var parsed = PolicyConfigurationValidator.ParseAuthentication(source.ToJsonString());
        Assert.Equal(AuthenticationMode.JWT, parsed.Mode);
        Assert.Equal("https://ISSUER.example/Realm", parsed.Jwt!.Issuer);
        Assert.Equal("  exact client  ", parsed.Jwt.ApplicationMappings[0].ClaimValue);
        Assert.Equal("rsa-1", parsed.Jwt.Jwks.GetProperty("keys")[0].GetProperty("kid").GetString());
        Assert.False(parsed.Jwt.ForwardBearer);
        Assert.False(new EffectiveRoutePolicies(false, null, null, null).HasApplicationIdentity);
        Assert.True(new EffectiveRoutePolicies(true, null, null, null).HasApplicationIdentity);
    }
    private static PolicyBindingConfiguration Binding(string type, string config) => new(Guid.NewGuid(), type, config, true, 0);
    [Theory]
    [InlineData("ApiKey")]
    [InlineData("Anonymous")]
    public void LegacyAuthNormalizationBytesUnchanged(string mode)
    {
        Assert.Equal("{\"mode\":\"" + mode + "\"}", PolicyConfigurationValidator.Normalize("authentication", "{ \"mode\" : \"" + mode + "\" }"));
        Reject("authentication", "{\"mode\":\"" + mode + "\",\"forwardBearer\":false}");
    }
}

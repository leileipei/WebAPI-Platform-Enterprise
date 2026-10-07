using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Security;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class JwtTokenVerifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private const string Subject = "private-sub-canary-7498";
    private const string Application = "private-app-canary-8362";
    private readonly JwtTokenVerifier verifier = new();
    internal sealed class KeyFixture : IDisposable
    {
        private readonly RSA? rsa;
        private readonly ECDsa? ec;
        internal string Algorithm { get; }
        internal JsonElement PublicKey { get; }
        internal KeyFixture(string algorithm = "RS256")
        {
            Algorithm = algorithm;
            if (algorithm == "RS256")
            {
                rsa = RSA.Create(2048);
                var p = rsa.ExportParameters(false);
                PublicKey = JsonSerializer.SerializeToElement(new { kty = "RSA", kid = "key-1", alg = algorithm, use = "sig", n = Encode(p.Modulus!), e = Encode(p.Exponent!) });
            }
            else
            {
                ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var p = ec.ExportParameters(false);
                PublicKey = JsonSerializer.SerializeToElement(new { kty = "EC", kid = "key-1", alg = algorithm, use = "sig", crv = "P-256", x = Encode(p.Q.X!), y = Encode(p.Q.Y!) });
            }
        }
        internal JwtAuthenticationConfiguration Configuration(string issuer = "https://issuer.example/realm") =>
            new(issuer, ["webapi-business"], [Algorithm], ["JWT", "at+jwt"], 30, 3600, "azp",
                [new(Application, Guid.Parse("11111111-1111-1111-1111-111111111111"))], JsonSerializer.SerializeToElement(new { keys = new[] { PublicKey } }), false);
        internal Dictionary<string, object?> Claims(string issuer = "https://issuer.example/realm") => new()
        {
            ["iss"] = issuer, ["aud"] = "webapi-business", ["sub"] = Subject, ["azp"] = Application,
            ["iat"] = Now.ToUnixTimeSeconds() - 60, ["exp"] = Now.ToUnixTimeSeconds() + 300
        };
        internal string Token(Dictionary<string, object?>? claims = null, Dictionary<string, object?>? header = null) =>
            Raw(JsonSerializer.Serialize(claims ?? Claims()), JsonSerializer.Serialize(header ?? Header()));
        internal Dictionary<string, object?> Header() => new() { ["alg"] = Algorithm, ["typ"] = "JWT", ["kid"] = "key-1" };
        internal string Raw(string claims, string? header = null)
        {
            var input = Encode(Encoding.UTF8.GetBytes(header ?? JsonSerializer.Serialize(Header()))) + "." + Encode(Encoding.UTF8.GetBytes(claims));
            var data = Encoding.ASCII.GetBytes(input);
            var signature = rsa is not null ? rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : ec!.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return input + "." + Encode(signature);
        }
        public void Dispose() { rsa?.Dispose(); ec?.Dispose(); }
    }
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private void Accept(KeyFixture key, string token, JwtAuthenticationConfiguration? config = null, DateTimeOffset? now = null) => Assert.True(verifier.Verify(token, config ?? key.Configuration(), now ?? Now).Success);
    private void Reject(KeyFixture key, string token, JwtAuthenticationConfiguration? config = null)
    {
        var result = verifier.Verify(token, config ?? key.Configuration(), Now);
        Assert.False(result.Success);
        Assert.Equal("invalid_jwt", result.FailureCode);
    }

    [Theory] [InlineData("RS256")] [InlineData("ES256")]
    public void Rs256AndEs256VerifyWithExactIssuerAudienceType(string algorithm)
    {
        using var key = new KeyFixture(algorithm);
        Accept(key, key.Token());
        var claims = key.Claims(); claims["aud"] = new[] { "other", "webapi-business" };
        var header = key.Header(); header["typ"] = "at+jwt";
        Accept(key, key.Token(claims, header));
        foreach (var issuer in new[] { "https://ISSUER.example/realm", "https://issuer.example/realm/", "https://issuer.example/Realm" })
        { claims = key.Claims(); claims["iss"] = issuer; Reject(key, key.Token(claims)); }
        foreach (var audience in new object?[] { "Webapi-business", "webapi-business/", null, Array.Empty<string>(), 123, new object[] { "webapi-business", 3 } })
        { claims = key.Claims(); claims["aud"] = audience; Reject(key, key.Token(claims)); }
        foreach (var type in new object?[] { "jwt", "At+jwt", null, "" })
        { header = key.Header(); header["typ"] = type; Reject(key, key.Token(header: header)); }
    }
    [Fact]
    public void RejectNoneHsConfusionBadSignatureUnknownKidAndTokenSuppliedUrls()
    {
        using var key = new KeyFixture(); Accept(key, key.Token());
        foreach (var algorithm in new[] { "none", "HS256", "ES256", "rs256" })
        { var header = key.Header(); header["alg"] = algorithm; Reject(key, key.Token(header: header)); }
        foreach (var kid in new object?[] { "unknown", "KEY-1", null, 1 })
        { var header = key.Header(); header["kid"] = kid; Reject(key, key.Token(header: header)); }
        foreach (var name in new[] { "jku", "x5u", "jwk", "crit", "b64", "enc", "zip", "x5c" })
        { var header = key.Header(); header[name] = "http://127.0.0.1:9/never-fetch"; Reject(key, key.Token(header: header)); }
        using var wrong = new KeyFixture(); Reject(key, wrong.Token());
        var valid = key.Token(); var segments = valid.Split('.'); segments[2] = Encode(new byte[256]); Reject(key, string.Join('.', segments));
        Reject(key, valid[..(valid.LastIndexOf('.') + 1)]);
    }
    [Fact]
    public void RejectDuplicateClaimsExtraSegmentsAndOver16KiB()
    {
        using var key = new KeyFixture(); Accept(key, key.Token());
        var claims = JsonSerializer.Serialize(key.Claims());
        Reject(key, key.Raw(claims[..^1] + ",\"sub\":\"other\"}"));
        Reject(key, key.Raw(claims[..^1] + ",\"s\\u0075b\":\"other\"}"));
        Reject(key, key.Raw(claims[..^1] + ",\"nested\":{\"x\":1,\"x\":2}}"));
        Reject(key, key.Raw(claims, "{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":\"key-1\",\"kid\":\"key-1\"}"));
        var token = key.Token(); Reject(key, token + ".extra"); Reject(key, token + ".extra.more");
        var oversized = key.Claims(); oversized["padding"] = new string('x', 16384); Reject(key, key.Token(oversized));
        Reject(key, token.Replace(".", "=.")); Reject(key, " " + token); Reject(key, token + "\n");
        var many = key.Claims(); for (var i = 0; i < 260; i++) many["c" + i] = i; Reject(key, key.Token(many));
        Reject(key, key.Raw(claims[..^1] + ",\"deep\":" + new string('[', 12) + "0" + new string(']', 12) + "}"));
        Reject(key, key.Raw(claims[..^1] + ",\"largeArray\":[" + string.Join(',', Enumerable.Repeat("0", 300)) + "]}"));
        // Noncanonical pad bits in a valid header must not alias the same token.
        var compact = key.Token(header: new() { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = "key-1", ["extra"] = "a" });
        var parts = compact.Split('.');
        var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        if (parts[0].Length % 4 != 0) { var n = alphabet.IndexOf(parts[0][^1]); parts[0] = parts[0][..^1] + alphabet[n | 1]; Reject(key, string.Join('.', parts)); }
    }
    [Fact]
    public void RequireFiniteDatesSubAndBoundedLifetime()
    {
        using var key = new KeyFixture(); Accept(key, key.Token());
        foreach (var name in new[] { "iat", "exp", "sub", "azp" })
        { var claims = key.Claims(); claims.Remove(name); Reject(key, key.Token(claims)); }
        foreach (var name in new[] { "iat", "exp", "nbf" })
        foreach (var value in new object?[] { null, "1800000000", new[] { 1800000000 }, false, double.MaxValue })
        { var claims = key.Claims(); claims[name] = value; Reject(key, key.Token(claims)); }
        foreach (var name in new[] { "sub", "azp" })
        foreach (var value in new object?[] { null, "", "   ", 1, new[] { "single" } })
        { var claims = key.Claims(); claims[name] = value; Reject(key, key.Token(claims)); }
        var tooLong = key.Claims(); tooLong["azp"] = new string('a', 257); Reject(key, key.Token(tooLong));
        var n = Now.ToUnixTimeSeconds();
        foreach (var dates in new[] { (n + 31, n + 300, (long?)null), (n - 31, n - 30, (long?)null),
            (n, n, (long?)null), (n - 3600, n + 1, (long?)null), (n - 60, n + 300, (long?)(n + 31)), (n - 60, n + 300, (long?)(n + 301)) })
        { var claims = key.Claims(); claims["iat"] = dates.Item1; claims["exp"] = dates.Item2; if (dates.Item3.HasValue) claims["nbf"] = dates.Item3.Value; Reject(key, key.Token(claims)); }
        var boundary = key.Claims(); boundary["iat"] = n + 30; boundary["exp"] = n + 300; boundary["nbf"] = n + 30; Accept(key, key.Token(boundary));
        boundary["iat"] = n - 3600; boundary["exp"] = n; boundary.Remove("nbf"); Accept(key, key.Token(boundary));
        Accept(key, key.Token(), key.Configuration() with { ClockSkewSeconds = 0 });
    }
    private sealed class Capture : EventListener
    {
        internal readonly System.Collections.Concurrent.ConcurrentBag<string> Messages = [];
        protected override void OnEventSourceCreated(EventSource eventSource)
        { if (eventSource.Name.Contains("IdentityModel", StringComparison.Ordinal)) EnableEvents(eventSource, EventLevel.Verbose); }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        { if (eventData.Payload is not null) foreach (var p in eventData.Payload) if (p is not null) Messages.Add(p.ToString()!); }
    }
    [Fact]
    public void NeverInvokesNetworkOrLogsClaims()
    {
        using var key = new KeyFixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var issuer = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/realm";
        var config = key.Configuration(issuer); var token = key.Token(key.Claims(issuer));
        using var logs = new Capture(); Accept(key, token, config);
        var claims = key.Claims(issuer); claims["exp"] = Now.ToUnixTimeSeconds() - 120; Reject(key, key.Token(claims), config);
        var header = key.Header(); header["jku"] = issuer + "/jwks"; Reject(key, key.Token(key.Claims(issuer), header), config);
        Assert.False(listener.Pending());
        var publicResult = JsonSerializer.Serialize(verifier.Verify(token, config, Now));
        Assert.DoesNotContain(Subject, publicResult); Assert.DoesNotContain(Application, publicResult); Assert.DoesNotContain(token, publicResult);
        Assert.All(logs.Messages, message => { Assert.DoesNotContain(Subject, message); Assert.DoesNotContain(Application, message); Assert.DoesNotContain(token, message); });
    }
}

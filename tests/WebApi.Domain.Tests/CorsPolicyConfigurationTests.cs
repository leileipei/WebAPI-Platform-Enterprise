using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Domain.Policies;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class CorsPolicyConfigurationTests
{
    public const string Source = """{"allowedOrigins":["HTTPS://Portal.Example.COM:443"],"allowCredentials":false,"allowedMethods":["get","POST"],"allowedHeaders":["Authorization","Content-Type","X-API-Key"],"exposedHeaders":["X-Upstream-Service"],"maxAgeSeconds":600}""";

    [Fact]
    public void Cors_ExplicitOrigin_NormalizesIdempotently()
    {
        var normalized = PolicyConfigurationValidator.Normalize("cors", Source);
        using var doc = JsonDocument.Parse(normalized);
        Assert.Equal("https://portal.example.com", doc.RootElement.GetProperty("allowedOrigins")[0].GetString());
        Assert.Equal("GET", doc.RootElement.GetProperty("allowedMethods")[0].GetString());
        Assert.Equal(normalized, PolicyConfigurationValidator.Normalize("cors", normalized));
    }

    [Fact]
    public void Cors_WildcardCredentials_Rejects()
    {
        var config = JsonNode.Parse(Source)!;
        config["allowedOrigins"] = new JsonArray("*"); config["allowCredentials"] = true;
        Reject(config.ToJsonString());
    }

    [Fact]
    public void Cors_WildcardWithoutCredentials_Accepts()
    {
        var config = JsonNode.Parse(Source)!; config["allowedOrigins"] = new JsonArray("*");
        using var doc = JsonDocument.Parse(PolicyConfigurationValidator.Normalize("cors", config.ToJsonString()));
        Assert.Equal("*", doc.RootElement.GetProperty("allowedOrigins")[0].GetString());
    }

    [Theory]
    [InlineData("https://portal.example.com/")]
    [InlineData("https://portal.example.com/path")]
    [InlineData("https://portal.example.com?x=1")]
    [InlineData("https://portal.example.com#fragment")]
    [InlineData("https://user:password@portal.example.com")]
    [InlineData("https://*.example.com")]
    [InlineData("ftp://portal.example.com")]
    [InlineData("null")]
    [InlineData(" https://portal.example.com")]
    [InlineData("https://portal.example.com\r\n")]
    [InlineData("https://portal.example.com\\other")]
    public void Cors_UnsafeOrigin_Rejects(string origin) => Reject(Change("allowedOrigins", new JsonArray(origin)));

    [Theory]
    [InlineData("http://EXAMPLE.com:80", "http://example.com")]
    [InlineData("http://example.com:8080", "http://example.com:8080")]
    [InlineData("https://[2001:db8::1]:443", "https://[2001:db8::1]")]
    public void Cors_OriginAuthority_NormalizesWithoutDroppingNonDefaultPort(string source, string expected)
    {
        using var doc = JsonDocument.Parse(PolicyConfigurationValidator.Normalize("cors", Change("allowedOrigins", new JsonArray(source))));
        Assert.Equal(expected, doc.RootElement.GetProperty("allowedOrigins")[0].GetString());
    }

    [Fact]
    public void Cors_NormalizedDuplicateOrigins_Rejects() =>
        Reject(Change("allowedOrigins", new JsonArray("https://example.com", "HTTPS://EXAMPLE.COM:443")));

    [Theory]
    [InlineData("allowedOrigins", 64, true)]
    [InlineData("allowedOrigins", 65, false)]
    [InlineData("allowedOrigins", 0, false)]
    [InlineData("allowedHeaders", 63, true)]
    [InlineData("allowedHeaders", 64, true)]
    [InlineData("allowedHeaders", 65, false)]
    [InlineData("exposedHeaders", 32, true)]
    [InlineData("exposedHeaders", 33, false)]
    public void Cors_ArrayBounds_AreEnforced(string field, int count, bool valid)
    {
        var items = new JsonArray(Enumerable.Range(0, count).Select(i => JsonValue.Create(field == "allowedOrigins" ? $"https://host{i}.example.com" : $"X-Business-{i}")).Cast<JsonNode?>().ToArray());
        Check(Change(field, items), valid);
    }

    [Theory]
    [InlineData("Host")][InlineData("Cookie")][InlineData("Set-Cookie")]
    [InlineData("X-WebApi-Application")][InlineData("Forwarded")][InlineData("X-Forwarded-For")]
    [InlineData("Connection")][InlineData("Keep-Alive")][InlineData("TE")][InlineData("Transfer-Encoding")]
    [InlineData("Trailer")][InlineData("Upgrade")][InlineData("Proxy-Authorization")][InlineData("Proxy-Authenticate")][InlineData("Proxy-Connection")]
    [InlineData("traceparent")][InlineData("tracestate")][InlineData("baggage")]
    [InlineData("*")][InlineData("X Invalid")][InlineData("X:Invalid")][InlineData("X\r\nInvalid")]
    public void Cors_ProtectedOrInvalidHeader_RejectsBothLists(string name)
    {
        Reject(Change("allowedHeaders", new JsonArray(name)));
        Reject(Change("exposedHeaders", new JsonArray(name)));
    }

    [Fact]
    public void Cors_HeaderDuplicates_AreCaseInsensitive() => Reject(Change("allowedHeaders", new JsonArray("X-Business", "x-business")));

    [Theory]
    [InlineData(0, true)][InlineData(86400, true)][InlineData(-1, false)][InlineData(86401, false)]
    public void Cors_MaxAgeBounds_AreEnforced(int value, bool valid) => Check(Change("maxAgeSeconds", JsonValue.Create(value)), valid);

    [Fact]
    public void Cors_FieldShapes_AreStrict()
    {
        Reject(Change("allowedOrigins", null));
        Reject(Change("allowedOrigins", new JsonArray((JsonNode?)null)));
        Reject(Change("allowCredentials", JsonValue.Create("false")));
        Reject(Change("allowedMethods", new JsonArray("GET", "get")));
        Reject(Change("allowedMethods", new JsonArray("TRACE")));
        Reject(Change("allowedOrigins", new JsonArray("*", "https://example.com")));
        foreach (var field in JsonNode.Parse(Source)!.AsObject().Select(p => p.Key).ToArray())
        { var config = JsonNode.Parse(Source)!.AsObject(); config.Remove(field); Reject(config.ToJsonString()); }
        Reject(Source.Replace("\"maxAgeSeconds\":600", "\"maxAgeSeconds\":600,\"extra\":true"));
        Reject(Source.Replace("\"maxAgeSeconds\":600", "\"maxAgeSeconds\":600,\"maxAgeSeconds\":601"));
        Reject(Change("allowedHeaders", new JsonArray(new string('X', 129))));
        Reject(Change("allowedOrigins", new JsonArray("https://example.com:" + new string('1', 500))));
    }

    [Fact]
    public void Cors_ExactUtf8ConfigBudget_AcceptsOnlyWithinLimit()
    {
        var padded = Source + new string(' ', 32768 - Encoding.UTF8.GetByteCount(Source));
        Assert.NotEmpty(PolicyConfigurationValidator.Normalize("cors", padded));
        Reject(padded + " ");
    }

    [Fact]
    public void Cors_TypedParse_ReturnsReadonlyIndependentCollections()
    {
        var config = PolicyConfigurationValidator.ParseCors(Source);
        Assert.Equal("https://portal.example.com", config.AllowedOrigins[0]);
        Assert.Equal(new[] { "GET", "POST" }, config.AllowedMethods);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.AllowedHeaders)[0] = "Host");
        string[] origins = ["https://example.com"], methods = ["GET"], headers = ["X-Business"], exposed = ["X-Result"];
        var copied = new WebApi.Contracts.Policies.CorsConfiguration(origins, false, methods, headers, exposed, 600);
        origins[0] = "https://other.example.com"; methods[0] = "TRACE"; headers[0] = "Host"; exposed[0] = "Cookie";
        Assert.Equal("https://example.com", copied.AllowedOrigins[0]);
        Assert.Equal("GET", copied.AllowedMethods[0]); Assert.Equal("X-Business", copied.AllowedHeaders[0]); Assert.Equal("X-Result", copied.ExposedHeaders[0]);
    }

    [Fact]
    public void Cors_SevenMethodsAndExactTokenLength_Accept()
    {
        var root = JsonNode.Parse(Source)!;
        root["allowedMethods"] = new JsonArray("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS");
        root["allowedHeaders"] = new JsonArray(new string('X', 128)); root["exposedHeaders"] = new JsonArray();
        var config = PolicyConfigurationValidator.ParseCors(root.ToJsonString());
        Assert.Equal(7, config.AllowedMethods.Count); Assert.Equal(128, config.AllowedHeaders[0].Length); Assert.Empty(config.ExposedHeaders);
    }

    private static string Change(string field, JsonNode? value)
    { var config = JsonNode.Parse(Source)!; config[field] = value; return config.ToJsonString(); }
    private static void Check(string config, bool valid)
    { if (valid) Assert.NotEmpty(PolicyConfigurationValidator.Normalize("cors", config)); else Reject(config); }
    private static void Reject(string config)
    {
        var error = Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("cors", config));
        Assert.Equal(422, error.Status); Assert.Equal("invalid_policy_config", error.Code);
    }
}

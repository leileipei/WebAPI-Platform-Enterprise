using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Domain.Policies;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class TransformPolicyConfigurationTests
{
    public const string Source = """{"requestHeaders":[{"action":"Set","name":"X-Partner-Version","value":"v2"}],"responseHeaders":[{"action":"Remove","name":"X-Debug"}],"path":{"removePrefix":"/gateway/","addPrefix":"/internal/v2/"},"query":[{"action":"Remove","name":"debug"},{"action":"Set","name":"region","value":"cn"}],"requestBody":null,"responseBody":null}""";

    [Fact]
    public void Transform_SetRemove_FieldShapesAreStrict()
    {
        var normalized = PolicyConfigurationValidator.Normalize("transform", Source);
        using var json = JsonDocument.Parse(normalized);
        Assert.False(json.RootElement.GetProperty("responseHeaders")[0].TryGetProperty("value", out _));
        Assert.Equal("/gateway", json.RootElement.GetProperty("path").GetProperty("removePrefix").GetString());
        Assert.Equal(normalized, PolicyConfigurationValidator.Normalize("transform", normalized));
        Reject(Source.Replace("\"action\":\"Remove\",\"name\":\"X-Debug\"", "\"action\":\"Remove\",\"name\":\"X-Debug\",\"value\":null"));
        Reject(Source.Replace(",\"value\":\"v2\"", ""));
        Reject(Source.Replace("\"Set\"", "\"set\""));
        Reject(Source.Replace("\"Set\"", "0"));
    }

    [Theory]
    [InlineData("Authorization")][InlineData("X-API-Key")][InlineData("Cookie")][InlineData("Set-Cookie")][InlineData("Host")]
    [InlineData("X-WebApi-Actor")][InlineData("X-Forwarded-For")][InlineData("Forwarded")]
    [InlineData("Connection")][InlineData("Keep-Alive")][InlineData("Proxy-Connection")][InlineData("Proxy-Authorization")][InlineData("Proxy-Authenticate")]
    [InlineData("TE")][InlineData("Transfer-Encoding")][InlineData("Trailer")][InlineData("Upgrade")]
    [InlineData("traceparent")][InlineData("tracestate")][InlineData("baggage")]
    [InlineData("Content-Length")][InlineData("Content-Type")][InlineData("Content-Encoding")]
    [InlineData("Vary")][InlineData("Cache-Control")][InlineData("Age")][InlineData("Expires")][InlineData("ETag")][InlineData("Last-Modified")][InlineData("Date")]
    [InlineData("Content-MD5")][InlineData("Digest")][InlineData("Content-Digest")][InlineData("Repr-Digest")]
    [InlineData("Signature")][InlineData("Signature-Input")][InlineData("Access-Control-Allow-Origin")]
    public void Transform_ProtectedHeader_Rejects(string header)
    {
        foreach (var direction in new[] { "requestHeaders", "responseHeaders" })
        {
            var root = JsonNode.Parse(Source)!;
            root[direction] = JsonNode.Parse(JsonSerializer.Serialize(new[] { new { action = "Set", name = header, value = "v2" } }));
            Reject(root.ToJsonString());
        }
    }

    [Theory]
    [InlineData("/a/../b")][InlineData("/a/./b")][InlineData("/a/%41")][InlineData("/a//b")]
    [InlineData("/a\\b")][InlineData("/{id}")][InlineData("/a?x=1")][InlineData("/a#x")][InlineData("https://host/path")][InlineData("relative")]
    public void Transform_Path_RejectsAmbiguousPrefix(string prefix)
    {
        foreach (var direction in new[] { "removePrefix", "addPrefix" })
        {
            var root = JsonNode.Parse(Source)!; root["path"]![direction] = prefix; Reject(root.ToJsonString());
        }
    }

    [Fact]
    public void Transform_QueryNames_AreCaseSensitiveButHeaderNamesAreNot()
    {
        var root = JsonNode.Parse(Source)!;
        root["query"] = JsonNode.Parse("""[{"action":"Set","name":"region","value":"cn"},{"action":"Set","name":"Region","value":"en"}]""");
        Assert.NotEmpty(PolicyConfigurationValidator.Normalize("transform", root.ToJsonString()));
        root["query"] = JsonNode.Parse("""[{"action":"Set","name":"region","value":"cn"},{"action":"Remove","name":"region"}]""");
        Reject(root.ToJsonString());
        root = JsonNode.Parse(Source)!;
        root["requestHeaders"] = JsonNode.Parse("""[{"action":"Set","name":"X-Business","value":"cn"},{"action":"Remove","name":"x-business"}]""");
        Reject(root.ToJsonString());
    }

    [Fact]
    public void Transform_OperationCountsAndUtf8Bounds_AreEnforced()
    {
        foreach (var field in new[] { "requestHeaders", "responseHeaders", "query" })
        {
            var root = JsonNode.Parse(Source)!;
            root[field] = JsonNode.Parse(JsonSerializer.Serialize(Enumerable.Range(0, 32).Select(i => new { action = "Set", name = "X-Business-" + i, value = "v" })));
            Assert.NotEmpty(PolicyConfigurationValidator.Normalize("transform", root.ToJsonString()));
            root[field]!.AsArray().Add(JsonNode.Parse("""{"action":"Remove","name":"X-Business-32"}""")); Reject(root.ToJsonString());
        }
        var config = JsonNode.Parse(Source)!;
        config["requestHeaders"]![0]!["value"] = new string('界', 341) + "x";
        Assert.NotEmpty(PolicyConfigurationValidator.Normalize("transform", config.ToJsonString()));
        config["requestHeaders"]![0]!["value"] = new string('界', 341) + "xy"; Reject(config.ToJsonString());
        config["requestHeaders"]![0]!["value"] = "\r\nInjected"; Reject(config.ToJsonString());
        config["requestHeaders"]![0]!["value"] = ""; Assert.NotEmpty(PolicyConfigurationValidator.Normalize("transform", config.ToJsonString()));
    }

    [Fact]
    public void Transform_AllSixFieldsAreRequired_EmptyTransformIsRejected()
    {
        foreach (var key in JsonNode.Parse(Source)!.AsObject().Select(p => p.Key).ToArray())
        { var root = JsonNode.Parse(Source)!.AsObject(); root.Remove(key); Reject(root.ToJsonString()); }
        Reject("""{"requestHeaders":[],"responseHeaders":[],"path":null,"query":[],"requestBody":null,"responseBody":null}""");
        Reject(Source.Replace("\"responseBody\":null", "\"responseBody\":null,\"script\":\"return input\""));
        Reject(Source.Replace("\"value\":\"v2\"", "\"value\":\"v2\",\"value\":\"v3\""));
    }

    [Fact]
    public void Transform_NormalizedUtf8Budget_RejectsEscapedExpansion()
    {
        var root = JsonNode.Parse(Source)!;
        root["requestHeaders"] = JsonNode.Parse(JsonSerializer.Serialize(Enumerable.Range(0, 17).Select(i => new {action="Set",name="X-Business-"+i,value=new string('界',341)+"x"})));
        var source = root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(source) < 32768);
        Reject(source);
    }

    [Fact]
    public void Transform_SpecExamples_RoundTripTypedParsing()
    {
        Assert.Equal("https://portal.example.com", PolicyConfigurationValidator.ParseCors(CorsPolicyConfigurationTests.Source).AllowedOrigins[0]);
        Assert.Equal("192.0.2.0/24", PolicyConfigurationValidator.ParseIpFilter("{\"mode\":\"AllowList\",\"ranges\":[\"192.0.2.0/24\",\"2001:db8::/32\"]}").Ranges[0]);
        var headers = PolicyConfigurationValidator.ParseTransform(Source);
        Assert.Null(headers.RequestBody); Assert.Null(headers.ResponseBody); Assert.Equal("/internal/v2", headers.Path!.AddPrefix);
        var body = PolicyConfigurationValidator.ParseTransform(JsonBodyConfigurationTests.Source);
        Assert.Equal("/customer_id", body.RequestBody!.Operations[0].Path); Assert.Equal("/result", body.ResponseBody!.Operations[0].Path);
        var root = JsonNode.Parse(Source)!; root["path"]!["removePrefix"] = null;
        using var json = JsonDocument.Parse(PolicyConfigurationValidator.Normalize("transform", root.ToJsonString()));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("path").GetProperty("removePrefix").ValueKind);
    }

    private static void Reject(string source) => Assert.Equal("invalid_policy_config", Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("transform", source)).Code);
}

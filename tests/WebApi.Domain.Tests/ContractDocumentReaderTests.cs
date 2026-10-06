using System.Text;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class ContractDocumentReaderTests
{
    private static readonly Uri Origin = new("https://contracts.example/openapi.yaml");
    private static ContractDocument Read(string text, string format = "json", ContractLimits? limits = null) =>
        new ContractDocumentReader().Read(new(Origin, text, format), limits ?? new(), default);
    internal const string Json = """
        {"openapi":"3.1.0","info":{"title":"订单","version":"1"},"paths":{"/orders":{"get":{"responses":{"200":{"description":"ok"}}}}},"x-future":{"value":true}}
        """;
    internal const string Yaml = """
        openapi: 3.1.0
        info:
          title: 订单
          version: '1'
        paths:
          /orders:
            get:
              responses:
                '200':
                  description: ok
        x-future:
          value: true
        """;

    [Fact] public void JsonAndYamlPreserveEquivalentContract()
    {
        var json = Read(Json); var yaml = Read(Yaml, "yaml");
        Assert.Equal(json.CanonicalJson, yaml.CanonicalJson);
        Assert.Equal(ContractDialect.Oas31, yaml.Dialect);
        Assert.Equal(Yaml, yaml.Source.RawText);
        Assert.True(yaml.Root["x-future"]!["value"]!.GetValue<bool>());
        Assert.Equal(2, yaml.Locations["/info"].Line);
    }

    [Theory]
    [InlineData("{\"openapi\":\"3.1.0\",\"openapi\":\"3.0.3\",\"info\":{},\"paths\":{}}", "json", "duplicate_key")]
    [InlineData("openapi: 3.1.0\nopenapi: 3.0.3", "yaml", "duplicate_key")]
    [InlineData("openapi: 3.1.0\nx: &a [*a]", "yaml", "yaml_alias_cycle")]
    [InlineData("openapi: !MyObject 3.1.0", "yaml", "unsupported_yaml_tag")]
    [InlineData("openapi: 3.1.0\n---\nopenapi: 3.0.3", "yaml", "multiple_yaml_documents")]
    public void DuplicateKeysAndAliasCycleAreRejected(string text, string format, string code)
    {
        var error = Assert.Throws<ApiException>(() => Read(text, format));
        Assert.Equal(code, error.Code);
    }

    [Fact] public void ExpansionCannotExceed50000NodesOr2MiB()
    {
        var large = Json.Replace("订单", new string('界', 700000));
        Assert.True(Encoding.UTF8.GetByteCount(large) > 2 * 1024 * 1024);
        Assert.Equal(413, Assert.Throws<ApiException>(() => Read(large)).Status);
        var expanded = Yaml + "\nx-nodes:\n  first: &a [1,2,3,4,5]\n  copies: [*a,*a,*a,*a,*a,*a,*a,*a,*a,*a]";
        Assert.Equal("contract_budget", Assert.Throws<ApiException>(() => Read(expanded, "yaml", new(MaxNodes: 50))).Code);
        var many = Json.Replace("\"x-future\":{\"value\":true}", "\"x-future\":[" + string.Join(',', Enumerable.Repeat("0", 50000)) + "]");
        Assert.Equal("contract_budget", Assert.Throws<ApiException>(() => Read(many)).Code);
    }

    [Fact] public void ScalarConversionDoesNotRoundLargeNumbers()
    {
        var doc = Read(Yaml + "\nx-values:\n  exact: 123456789012345678901234567890.123456789\n  date: 2026-10-07\n  oldBoolean: yes\n  nothing: null\n  quoted: 'true'", "yaml");
        Assert.Equal("123456789012345678901234567890.123456789", doc.Root["x-values"]!["exact"]!.ToJsonString());
        Assert.Equal("2026-10-07", doc.Root["x-values"]!["date"]!.GetValue<string>());
        Assert.Equal("yes", doc.Root["x-values"]!["oldBoolean"]!.GetValue<string>());
        Assert.Null(doc.Root["x-values"]!["nothing"]);
        Assert.Equal("true", doc.Root["x-values"]!["quoted"]!.GetValue<string>());
    }

    [Fact] public void UnknownFieldsRemainInRoot()
    {
        var doc = Read(Json.Replace("\"value\":true", "\"value\":true,\"future\":{\"a/b~\":{\"keyword\":1}}"));
        Assert.Equal(1, doc.Root["x-future"]!["future"]!["a/b~"]!["keyword"]!.GetValue<int>());
        Assert.Contains("/x-future/future/a~1b~0/keyword", doc.Locations.Keys);
    }

    [Theory]
    [InlineData("{\"openapi\":\"3.2.0\",\"info\":{\"title\":\"t\",\"version\":\"1\"},\"paths\":{}}")]
    [InlineData("{\"openapi\":\"3.1.0\",\"info\":{},\"paths\":{}}")]
    [InlineData("{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"t\",\"version\":\"1\"},\"paths\":{\"/x\":{\"get\":{\"responses\":[]}}}}")]
    public void InvalidOpenApiShapeIsRejected(string text) => Assert.Throws<ApiException>(() => Read(text));

    [Fact] public void SafeMergeMappingAndAliasHaveDeterministicValues()
    {
        var doc = Read(Yaml + "\nx-base: &a {a: 1}\nx-merged: {<<: *a, b: 2}", "yaml");
        Assert.Equal(1, doc.Root["x-merged"]!["a"]!.GetValue<int>());
        Assert.Equal(2, doc.Root["x-merged"]!["b"]!.GetValue<int>());
        Assert.Equal("duplicate_key", Assert.Throws<ApiException>(() => Read(Yaml + "\nx-base: &a {a: 1}\nx-merged: {<<: *a, a: 2}", "yaml")).Code);
    }

    [Fact] public void CancelledReadingDoesNotReturnSuccessfulContract()
    {
        var ct = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => new ContractDocumentReader().Read(new(Origin, Json, "json"), new(), ct));
    }

    [Theory]
    [InlineData("!!bool 123")]
    [InlineData("!!int false")]
    [InlineData("!!null true")]
    [InlineData("!!map false")]
    [InlineData("!!seq {}")]
    public void ExplicitTagsCannotChangeTheDeclaredJsonType(string value) =>
        Assert.Equal("invalid_yaml_scalar", Assert.Throws<ApiException>(() => Read(Yaml + "\nx-value: " + value, "yaml")).Code);

    [Fact] public void CanonicalAliasExpansionHasItsOwnByteLimit()
    {
        var text = Yaml + "\nx-base: &large '" + new string('x', 10000) + "'\nx-copies: [*large,*large,*large]";
        Assert.True(Encoding.UTF8.GetByteCount(text) < 20000);
        Assert.Equal(413, Assert.Throws<ApiException>(() => Read(text, "yaml", new(MaxDocumentBytes: 20000))).Status);
    }

    [Fact] public void LocationIsOneBasedForJsonAndYaml()
    {
        Assert.Equal(1, Read(Json).Locations[""].Line);
        var yaml = Read(Yaml, "yaml");
        Assert.Equal(3, yaml.Locations["/info/title"].Line);
        Assert.Equal(3, yaml.Locations["/info/title"].Column);
    }

    [Fact] public void OperationCountIsBounded()
    {
        var root = JsonNode.Parse(Json)!;
        root["paths"]!["/second"] = root["paths"]!["/orders"]!.DeepClone();
        Assert.Equal("contract_budget", Assert.Throws<ApiException>(() => Read(root.ToJsonString(), limits: new(MaxOperations: 1))).Code);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    public void SourceLocationPointersCannotAmplifySmallInputWithoutLimit(string format)
    {
        var name = new string('x', 900);
        var text = format == "json" ? Json.Replace("\"x-future\":{\"value\":true}", "\"" + name + "\":[0,0,0,0,0,0]") : Yaml + "\n" + name + ": [0,0,0,0,0,0]";
        Assert.True(Encoding.UTF8.GetByteCount(text) < 2000);
        Assert.Equal("contract_budget", Assert.Throws<ApiException>(() => Read(text, format, new(MaxDocumentBytes: 2000))).Code);
    }
}

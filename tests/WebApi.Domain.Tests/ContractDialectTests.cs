using System.Text.Json.Nodes;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class ContractDialectTests
{
    [Fact] public void Oas30RefSiblingsDifferFromOas31()
    {
        var original = JsonNode.Parse("{\"$ref\":\"#/x\",\"type\":\"string\",\"maxLength\":2}")!;
        var thirty = DialectAdapter.PrepareSchema(original, ContractDialect.Oas30, "request");
        var thirtyOne = DialectAdapter.PrepareSchema(original, ContractDialect.Oas31, "request");
        Assert.Null(thirty.Node["maxLength"]);
        Assert.Equal(2, thirtyOne.Node["maxLength"]!.GetValue<int>());
        Assert.Equal(2, original["maxLength"]!.GetValue<int>());
    }

    [Fact] public void NullableAndExclusiveBoundsUseTheDeclaredDialect()
    {
        var original = JsonNode.Parse("{\"type\":\"integer\",\"nullable\":true,\"minimum\":2,\"exclusiveMinimum\":true}")!;
        var prepared = DialectAdapter.PrepareSchema(original, ContractDialect.Oas30, "request");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[\"integer\",\"null\"]"), prepared.Node["type"]));
        Assert.Equal(2, prepared.Node["exclusiveMinimum"]!.GetValue<int>());
        Assert.Null(prepared.Node["minimum"]);
        Assert.Null(prepared.Node["nullable"]);
        Assert.NotNull(original["nullable"]);
        var thirtyOne = DialectAdapter.PrepareSchema(original, ContractDialect.Oas31, "request");
        Assert.Contains(thirtyOne.Issues, x => x.Code == "unsupported_schema_keyword" && x.Pointer == "/nullable");
        Assert.Equal("integer", thirtyOne.Node["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("request", "id", "secret")]
    [InlineData("response", "secret", "id")]
    public void ReadOnlyWriteOnlyRequirednessFollowsDirection(string direction, string removed, string kept)
    {
        var schema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"readOnly\":true},\"secret\":{\"type\":\"string\",\"writeOnly\":true}},\"required\":[\"id\",\"secret\"],\"x-keep\":42}")!;
        var prepared = DialectAdapter.PrepareSchema(schema, ContractDialect.Oas30, direction);
        Assert.DoesNotContain(prepared.Node["required"]!.AsArray(), x => x!.GetValue<string>() == removed);
        Assert.Contains(prepared.Node["required"]!.AsArray(), x => x!.GetValue<string>() == kept);
        Assert.Equal(42, prepared.Node["x-keep"]!.GetValue<int>());
        Assert.Contains(prepared.Issues, x => x.Code == "unsupported_schema_keyword" && x.Pointer == "/x-keep");
        Assert.Equal(2, schema["required"]!.AsArray().Count);
    }

    [Fact] public void Oas31BooleanSchemaIsPreservedAndOas30ReportsInvalidity()
    {
        Assert.False(DialectAdapter.PrepareSchema(JsonValue.Create(false)!, ContractDialect.Oas31, "request").Node.GetValue<bool>());
        Assert.Contains(DialectAdapter.PrepareSchema(JsonValue.Create(false)!, ContractDialect.Oas30, "request").Issues, x => x.Code == "invalid_schema_dialect");
    }

    [Fact] public void AdaptationHasCumulativeNodeBudgetIncludingAnnotationData()
    {
        var schema = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enumerable.Range(0, 50000).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) };
        Assert.Throws<WebApi.Contracts.Common.ApiException>(() => DialectAdapter.PrepareSchema(schema, ContractDialect.Oas31, "request"));
    }
}

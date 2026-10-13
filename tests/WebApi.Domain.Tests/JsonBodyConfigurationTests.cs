using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Domain.Policies;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class JsonBodyConfigurationTests
{
    public const string Source = """{"requestHeaders":[],"responseHeaders":[],"path":null,"query":[],"requestBody":{"methods":["POST","PUT","PATCH"],"maxBodyBytes":262144,"operations":[{"op":"move","from":"/user_id","path":"/customer_id"},{"op":"add","path":"/source","value":"webapi"}]},"responseBody":{"statusCodes":[200],"maxBodyBytes":262144,"operations":[{"op":"move","from":"/data","path":"/result"},{"op":"remove","path":"/debug"}]}}""";

    [Fact]
    public void JsonBody_CompleteTwoDirectionExample_NormalizesIdempotently()
    {
        var normalized = PolicyConfigurationValidator.Normalize("transform", Source);
        Assert.Equal(normalized, PolicyConfigurationValidator.Normalize("transform", normalized));
        using var json = JsonDocument.Parse(normalized);
        Assert.Equal("move", json.RootElement.GetProperty("responseBody").GetProperty("operations")[0].GetProperty("op").GetString());
    }

    [Theory]
    [InlineData(1024, true)][InlineData(1048576, true)][InlineData(1023, false)][InlineData(1048577, false)]
    public void JsonBody_ByteBudget_EnforcesBothDirectionBounds(int bytes, bool valid)
    {
        foreach (var direction in new[] { "requestBody", "responseBody" })
        { var root = JsonNode.Parse(Source)!; root[direction]!["maxBodyBytes"] = bytes; Check(root.ToJsonString(), valid); }
    }

    [Fact]
    public void JsonBody_OperationCount_IsBounded()
    {
        var root = JsonNode.Parse(Source)!;
        root["requestBody"]!["operations"] = JsonNode.Parse(JsonSerializer.Serialize(Enumerable.Range(0, 32).Select(i => new {op="add",path="/x"+i,value=i})));
        Check(root.ToJsonString(), true);
        root["requestBody"]!["operations"]!.AsArray().Add(JsonNode.Parse("""{"op":"remove","path":"/x"}""")); Check(root.ToJsonString(), false);
        root["requestBody"]!["operations"] = new JsonArray(); Check(root.ToJsonString(), false);
    }

    [Theory]
    [InlineData("{\"op\":\"add\",\"path\":\"/x\",\"value\":null}", true)]
    [InlineData("{\"op\":\"add\",\"path\":\"/x\"}", false)]
    [InlineData("{\"op\":\"replace\",\"path\":\"/x\",\"value\":9007199254740993}", true)]
    [InlineData("{\"op\":\"remove\",\"path\":\"/x\",\"value\":null}", false)]
    [InlineData("{\"op\":\"copy\",\"from\":\"/x\",\"path\":\"/y\"}", true)]
    [InlineData("{\"op\":\"copy\",\"path\":\"/y\"}", false)]
    [InlineData("{\"op\":\"move\",\"from\":\"/x\",\"path\":\"/x/child\"}", false)]
    [InlineData("{\"op\":\"move\",\"from\":\"\",\"path\":\"/child\"}", false)]
    [InlineData("{\"op\":\"add\",\"path\":\"\",\"value\":null}", true)]
    [InlineData("{\"op\":\"remove\",\"path\":\"\"}", false)]
    [InlineData("{\"op\":\"remove\",\"path\":\"#/%2Fx\"}", false)]
    [InlineData("{\"op\":\"remove\",\"path\":\"/x~2\"}", false)]
    [InlineData("{\"op\":\"remove\",\"path\":\"/x~01\"}", true)]
    [InlineData("{\"op\":\"remove\",\"path\":\"/%2Fx\"}", true)]
    [InlineData("{\"op\":\"test\",\"path\":\"/x\",\"value\":1}", false)]
    public void JsonBody_OperationFieldsAndPointerSemantics_AreStrict(string operation, bool valid)
    {
        var root = JsonNode.Parse(Source)!; root["requestBody"]!["operations"] = JsonNode.Parse("[" + operation + "]"); Check(root.ToJsonString(), valid);
    }

    [Theory]
    [InlineData(200, true)][InlineData(599, true)][InlineData(199, false)][InlineData(600, false)]
    [InlineData(204, false)][InlineData(205, false)][InlineData(206, false)][InlineData(304, false)]
    public void JsonBody_ResponseStatusSelection_RejectsUnmappableStatuses(int status, bool valid)
    { var root = JsonNode.Parse(Source)!; root["responseBody"]!["statusCodes"] = new JsonArray(status); Check(root.ToJsonString(), valid); }

    [Fact]
    public void JsonBody_RequestHeadAndDuplicates_AreRejected()
    {
        var root = JsonNode.Parse(Source)!; root["requestBody"]!["methods"] = new JsonArray("HEAD"); Check(root.ToJsonString(), false);
        root["requestBody"]!["methods"] = new JsonArray("POST", "POST"); Check(root.ToJsonString(), false);
        root["requestBody"]!["methods"] = new JsonArray(); Check(root.ToJsonString(), false);
        root = JsonNode.Parse(Source)!; root["responseBody"]!["statusCodes"] = new JsonArray(200, 200); Check(root.ToJsonString(), false);
    }

    [Fact]
    public void JsonBody_ValueNullAndLargeNumber_AreNotLost()
    {
        var root = JsonNode.Parse(Source)!;
        root["requestBody"]!["operations"] = JsonNode.Parse("""[{"op":"add","path":"/number","value":9007199254740993},{"op":"add","path":"/nullable","value":null}]""");
        using var json = JsonDocument.Parse(PolicyConfigurationValidator.Normalize("transform", root.ToJsonString()));
        var ops = json.RootElement.GetProperty("requestBody").GetProperty("operations");
        Assert.Equal("9007199254740993", ops[0].GetProperty("value").GetRawText()); Assert.Equal(JsonValueKind.Null, ops[1].GetProperty("value").ValueKind);
    }

    [Fact]
    public void JsonBody_ValueAndPointerBudgets_AreEnforced()
    {
        var root = JsonNode.Parse(Source)!;
        root["requestBody"]!["operations"] = JsonNode.Parse("[{\"op\":\"add\",\"path\":\"/x\",\"value\":\"" + new string('a', 8190) + "\"}]"); Check(root.ToJsonString(), true);
        root["requestBody"]!["operations"]![0]!["value"] = new string('a', 8191); Check(root.ToJsonString(), false);
        root["requestBody"]!["operations"]![0]!["value"] = 1;
        root["requestBody"]!["operations"]![0]!["path"] = "/" + new string('a', 511); Check(root.ToJsonString(), true);
        root["requestBody"]!["operations"]![0]!["path"] = "/" + new string('a', 512); Check(root.ToJsonString(), false);
        root["requestBody"]!["operations"]![0]!["path"] = string.Concat(Enumerable.Repeat("/x", 32)); Check(root.ToJsonString(), true);
        root["requestBody"]!["operations"]![0]!["path"] = string.Concat(Enumerable.Repeat("/x", 33)); Check(root.ToJsonString(), false);
        root["requestBody"]!["operations"]![0]!["path"] = "/x\n"; Check(root.ToJsonString(), false);
    }

    [Fact]
    public void JsonBody_TypedParse_KeepsNullPresenceAndLiteralPointerTokens()
    {
        var root = JsonNode.Parse(Source)!;
        root["requestBody"]!["operations"] = JsonNode.Parse("[{\"op\":\"add\",\"path\":\"/x~01\",\"value\":null},{\"op\":\"remove\",\"path\":\"/%2Fx\"}]");
        var config = PolicyConfigurationValidator.ParseTransform(root.ToJsonString());
        Assert.True(config.RequestBody!.Operations[0].Value.HasValue);
        Assert.Equal(JsonValueKind.Null, config.RequestBody.Operations[0].Value!.Value.ValueKind);
        Assert.False(config.RequestBody.Operations[1].Value.HasValue);
        Assert.Equal(new[] { "x~1", "%2Fx" }, JsonPointerPath.Parse("/x~01/%2Fx"));
        Assert.Empty(JsonPointerPath.Parse(""));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.RequestBody.Methods)[0] = "HEAD");
    }

    private static void Check(string source, bool valid)
    { if (valid) Assert.NotEmpty(PolicyConfigurationValidator.Normalize("transform", source)); else Assert.Equal("invalid_policy_config", Assert.Throws<ApiException>(() => PolicyConfigurationValidator.Normalize("transform", source)).Code); }
}

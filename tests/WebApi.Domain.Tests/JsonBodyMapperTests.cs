using System.Text;
using System.Text.Json;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class JsonBodyMapperTests
{
    [Fact]
    public void JsonBody_MoveAndRemove_ProducesExpectedFields()
    {
        var config = PolicyConfigurationValidator.ParseTransform(JsonBodyConfigurationTests.Source);
        var request = Apply("""{"user_id":123,"items":["A","B"]}""", config.RequestBody!.Operations);
        using var doc = JsonDocument.Parse(Success(request));
        Assert.Equal(123, doc.RootElement.GetProperty("customer_id").GetInt32());
        Assert.Equal("webapi", doc.RootElement.GetProperty("source").GetString()); Assert.False(doc.RootElement.TryGetProperty("user_id", out _));
        var response = Apply("""{"data":{"stock":10},"debug":"internal"}""", config.ResponseBody!.Operations);
        using var output = JsonDocument.Parse(Success(response));
        Assert.Equal(10, output.RootElement.GetProperty("result").GetProperty("stock").GetInt32()); Assert.False(output.RootElement.TryGetProperty("debug", out _));
    }

    [Fact]
    public void JsonBody_CopyDoesNotAliasSource()
    {
        var result = Apply("""{"source":{"value":1}}""", Ops("""[{"op":"copy","from":"/source","path":"/target"},{"op":"replace","path":"/target/value","value":2}]"""));
        using var doc = JsonDocument.Parse(Success(result));
        Assert.Equal(1, doc.RootElement.GetProperty("source").GetProperty("value").GetInt32()); Assert.Equal(2, doc.RootElement.GetProperty("target").GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData("[1,2]", "[{\"op\":\"add\",\"path\":\"/-\",\"value\":3}]", "[1,2,3]")]
    [InlineData("[1,2]", "[{\"op\":\"add\",\"path\":\"/1\",\"value\":3}]", "[1,3,2]")]
    [InlineData("[1,2,3]", "[{\"op\":\"move\",\"from\":\"/0\",\"path\":\"/2\"}]", "[2,3,1]")]
    [InlineData("[1,2]", "[{\"op\":\"copy\",\"from\":\"/0\",\"path\":\"/2\"}]", "[1,2,1]")]
    [InlineData("{\"x\":1}", "[{\"op\":\"replace\",\"path\":\"\",\"value\":null}]", "null")]
    [InlineData("null", "[{\"op\":\"add\",\"path\":\"\",\"value\":{\"x\":1}}]", "{\"x\":1}")]
    [InlineData("{\"x\":null}", "[{\"op\":\"copy\",\"from\":\"/x\",\"path\":\"\"}]", "null")]
    [InlineData("{\"x\":{\"y\":1}}", "[{\"op\":\"move\",\"from\":\"/x\",\"path\":\"\"}]", "{\"y\":1}")]
    [InlineData("{\"x\":1}", "[{\"op\":\"move\",\"from\":\"/x\",\"path\":\"/x\"}]", "{\"x\":1}")]
    [InlineData("{\"a/b\":1,\"~1\":2,\"%2Fx\":3}", "[{\"op\":\"remove\",\"path\":\"/a~1b\"},{\"op\":\"replace\",\"path\":\"/~01\",\"value\":4}]", "{\"~1\":4,\"%2Fx\":3}")]
    [InlineData("{\"-\":1}", "[{\"op\":\"remove\",\"path\":\"/-\"}]", "{}")]
    public void JsonBody_RootArrayAndEscapedMembers_HaveExactSemantics(string input, string operations, string expected)
    {
        var result = Apply(input, Ops(operations));
        using var actual = JsonDocument.Parse(Success(result)); using var want = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(want.RootElement, actual.RootElement));
    }

    [Fact]
    public void JsonBody_NumbersKeepRawPrecision()
    {
        var result = Apply("""{"big":9007199254740993,"decimal":0.12345678901234567890123456789}""", Ops("""[{"op":"add","path":"/nullable","value":null}]"""));
        using var doc = JsonDocument.Parse(Success(result));
        Assert.Equal("9007199254740993", doc.RootElement.GetProperty("big").GetRawText());
        Assert.Equal("0.12345678901234567890123456789", doc.RootElement.GetProperty("decimal").GetRawText());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("nullable").ValueKind);
    }

    [Fact]
    public void JsonBody_SecondOperationFailure_IsAtomicAndInputUnchanged()
    {
        var input = Encoding.UTF8.GetBytes("{\"x\":1}"); var saved = input.ToArray();
        var result = ApplyBytes(input, Compile(Ops("""[{"op":"replace","path":"/x","value":2},{"op":"remove","path":"/missing"}]""")), 1024, default);
        Assert.Null(result.Utf8); Assert.Equal("transform_json_operation_failed", result.Reason); Assert.Equal(2, result.OperationIndex); Assert.Equal(saved, input);
    }

    [Theory]
    [InlineData("[1]", "[{\"op\":\"remove\",\"path\":\"/01\"}]")]
    [InlineData("[1]", "[{\"op\":\"replace\",\"path\":\"/-\",\"value\":2}]")]
    [InlineData("[1]", "[{\"op\":\"add\",\"path\":\"/2\",\"value\":2}]")]
    [InlineData("{\"x\":null}", "[{\"op\":\"add\",\"path\":\"/x/y\",\"value\":2}]")]
    [InlineData("{}", "[{\"op\":\"replace\",\"path\":\"/x\",\"value\":2}]")]
    [InlineData("{}", "[{\"op\":\"copy\",\"from\":\"/x\",\"path\":\"/y\"}]")]
    public void JsonBody_MissingParentsAndInvalidArrayIndices_FailWithoutOutput(string input, string operations)
    {
        var result = Apply(input, Ops(operations)); Assert.Null(result.Utf8);
        Assert.Equal("transform_json_operation_failed", result.Reason); Assert.Equal(1, result.OperationIndex);
    }

    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")][InlineData("{\"nested\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"x\":1} true")][InlineData("/*comment*/{}")][InlineData("{\"x\":1,}")][InlineData("")]
    [InlineData("{\"x\":\"\\uD800\"}")][InlineData("{\"x\":\"\\uDC00\"}")]
    public void JsonBody_InvalidJson_FailsWithControlledReason(string input)
    {
        var result = Apply(input, Ops("""[{"op":"add","path":"/x","value":1}]"""));
        Assert.Null(result.Utf8); Assert.Equal("transform_json_invalid", result.Reason); Assert.Null(result.OperationIndex);
    }

    [Fact]
    public void JsonBody_BomAcceptedButInvalidUtf8Rejected()
    {
        var mapping = Compile(Ops("""[{"op":"add","path":"/x","value":1}]"""));
        byte[] bom = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("{}")];
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString(Success(ApplyBytes(bom, mapping, 1024, default))));
        byte[] bad = [0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0xff, 0x22, 0x7d];
        var result = ApplyBytes(bad, mapping, 1024, default); Assert.Null(result.Utf8); Assert.Equal("transform_json_invalid", result.Reason);
    }

    [Fact]
    public void JsonBody_ExactDepthAndIntermediateBudget_AreEnforced()
    {
        var mapping = Compile(Ops("""[{"op":"replace","path":"","value":null}]"""));
        var at = new string('[', 32) + "0" + new string(']', 32);
        Assert.Equal("null", Encoding.UTF8.GetString(Success(ApplyBytes(Encoding.UTF8.GetBytes(at), mapping, 1024, default))));
        var tooDeep = new string('[', 33) + "0" + new string(']', 33);
        Assert.Equal("transform_json_invalid", ApplyBytes(Encoding.UTF8.GetBytes(tooDeep), mapping, 1024, default).Reason);
        var result = Apply("{\"x\":\"" + new string('a', 600) + "\"}", Ops("""[{"op":"copy","from":"/x","path":"/y"},{"op":"remove","path":"/y"}]"""), 1024);
        Assert.Null(result.Utf8); Assert.Equal("transform_body_too_large", result.Reason); Assert.Equal(1, result.OperationIndex);
    }

    [Fact]
    public async Task JsonBody_SameCompiledMapping_IsSafeForConcurrentCallsAndCancellation()
    {
        var operations = Ops("""[{"op":"add","path":"/x","value":{"v":1}},{"op":"replace","path":"/x/v","value":2}]"""); var mapping = Compile(operations);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => ApplyBytes(Encoding.UTF8.GetBytes("{}"), mapping, 1024, default))));
        Assert.All(results, r => Assert.Equal("{\"x\":{\"v\":2}}", Encoding.UTF8.GetString(Success(r))));
        using var ct = new CancellationTokenSource(); ct.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ApplyBytes(Encoding.UTF8.GetBytes("{}"), mapping, 1024, ct.Token));
        Assert.Equal(1, operations[0].Value!.Value.GetProperty("v").GetInt32());
    }

    [Fact]
    public void JsonBody_All32Operations_CompleteBeforeCancelledReuse()
    {
        var mapping = Compile(Ops(JsonSerializer.Serialize(Enumerable.Range(0, 32).Select(i => new {op="add",path="/x"+i,value=i}))));
        Assert.Equal(32, mapping.OperationCount);
        using var doc = JsonDocument.Parse(Success(ApplyBytes(Encoding.UTF8.GetBytes("{}"), mapping, 1024, default)));
        Assert.Equal(32, doc.RootElement.EnumerateObject().Count());
        using var ct = new CancellationTokenSource(); ct.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ApplyBytes(Encoding.UTF8.GetBytes("{}"), mapping, 1024, ct.Token));
    }

    [Fact]
    public void JsonBody_InputBudget_IsIndependentFromSmallOutput()
    {
        var mapping = Compile(Ops("[{\"op\":\"add\",\"path\":\"/x\",\"value\":1}]"));
        var input = "{}" + new string(' ', 1022);
        Assert.NotNull(ApplyBytes(Encoding.UTF8.GetBytes(input), mapping, 1024, default).Utf8);
        var large = ApplyBytes(Encoding.UTF8.GetBytes(input + " "), mapping, 1024, default);
        Assert.Null(large.Utf8); Assert.Equal("transform_body_too_large", large.Reason); Assert.Null(large.OperationIndex);
    }

    [Fact]
    public void JsonBody_ConstantsOutliveSourceDocument()
    {
        JsonBodyOperation operation;
        using (var doc = JsonDocument.Parse("{\"v\":9007199254740993}")) operation = new("add", "/x", null, doc.RootElement);
        var mapping = Compile([operation]);
        using var output = JsonDocument.Parse(Success(ApplyBytes(Encoding.UTF8.GetBytes("{}"), mapping, 1024, default)));
        Assert.Equal("9007199254740993", output.RootElement.GetProperty("x").GetProperty("v").GetRawText());
    }

    private static IReadOnlyList<JsonBodyOperation> Ops(string source)
    {
        var config = "{\"requestHeaders\":[],\"responseHeaders\":[],\"path\":null,\"query\":[],\"requestBody\":{\"methods\":[\"POST\"],\"maxBodyBytes\":262144,\"operations\":" + source + "},\"responseBody\":null}";
        return PolicyConfigurationValidator.ParseTransform(config).RequestBody!.Operations;
    }
    private static TestResult Apply(string input, IReadOnlyList<JsonBodyOperation> operations, int maxBytes = 262144) => ApplyBytes(Encoding.UTF8.GetBytes(input), Compile(operations), maxBytes, default);
    private static byte[] Success(TestResult result) { Assert.Null(result.Reason); return Assert.IsType<byte[]>(result.Utf8); }
    private sealed record TestResult(byte[]? Utf8, string? Reason, int? OperationIndex);
    private static CompiledJsonBodyMapping Compile(IReadOnlyList<JsonBodyOperation> operations) => JsonBodyMapper.Compile(operations);
    private static TestResult ApplyBytes(byte[] bytes, CompiledJsonBodyMapping mapping, int maxBytes, CancellationToken ct)
    {
        var result = JsonBodyMapper.Apply(bytes, mapping, maxBytes, ct);
        return new(result.Utf8, result.Reason, result.OperationIndex);
    }
}

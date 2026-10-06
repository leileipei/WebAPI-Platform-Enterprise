using System.Text;
using System.Text.Json;
using WebApi.Contracts.Comparisons;
using WebApi.Infrastructure.Comparisons;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class ComparisonProcessTests
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web){MaxDepth=128};
    [Fact]public async Task ActualBuiltRuntimeToolComparesWithoutCallingItsOwnRunner()
    {
        var path=Environment.GetEnvironmentVariable("WEBAPI_RUNTIME_TOOL_DLL");Assert.True(File.Exists(path));var runner=new ContractProcessRunner(new(path!));
        var input=new ComparisonInput(ComparisonTestData.Version(OpenApiCompatibilityV2Tests.Root().ToJsonString()),ComparisonTestData.Version(OpenApiCompatibilityV2Tests.Root("{\"type\":\"number\"}").ToJsonString()));
        var request=new ContractProcessRequest("compare",JsonSerializer.SerializeToElement(new{inputUtf8Base64=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(input,JsonOptions)),limits=new ComparisonLimits()}));
        var response=await runner.RunAsync(request,TimeSpan.FromSeconds(10),default);Assert.Equal("Valid",response.Status);Assert.Empty(response.Issues);Assert.NotNull(response.Result);
        var report=response.Result.Value.Deserialize<ComparisonReport>(JsonOptions)!;Assert.Equal("compatibility-v2",report.EngineVersion);Assert.Equal("Complete",report.Coverage);Assert.Equal(0,report.Counts.Breaking);Assert.Equal(ContractNormalizer.Fingerprint(input,"compatibility-v2"),report.InputFingerprint);
    }
    [Fact]public async Task V2ReportBindsRawInputBytesAndExecutedRuleIds()
    {
        var path=Environment.GetEnvironmentVariable("WEBAPI_RUNTIME_TOOL_DLL");Assert.True(File.Exists(path));var runner=new ContractProcessRunner(new(path!));
        var input=new ComparisonInput(ComparisonTestData.Version(OpenApiCompatibilityV2Tests.Root().ToJsonString()),ComparisonTestData.Version(OpenApiCompatibilityV2Tests.Root("{\"type\":\"number\"}").ToJsonString()));var response=await runner.RunAsync(ContractProcessProtocol.ComparisonRequest(input),TimeSpan.FromSeconds(10),default);Assert.Equal("Valid",response.Status);
        Assert.True(response.Result!.Value.TryGetProperty("provenance",out var provenance),"新证据缺少原始输入摘要来源信息");Assert.Equal(ContractNormalizer.Hash(WebApi.Contracts.Common.CanonicalJson.Serialize(input)),provenance.GetProperty("inputHash").GetString());Assert.Equal("oas-http-model-v2",provenance.GetProperty("adapterVersion").GetString());Assert.Equal("Annotation",provenance.GetProperty("formatMode").GetString());Assert.Contains(provenance.GetProperty("appliedRuleIds").EnumerateArray(),x=>x.GetString()=="schema.types");
    }
}

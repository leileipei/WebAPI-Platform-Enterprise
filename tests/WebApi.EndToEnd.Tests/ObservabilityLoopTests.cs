using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;
[Trait("Scenario","Observability")]
public sealed class ObservabilityLoopTests
{
    [Fact] public async Task RealTwoGatewayCountersLogsAndServerClientSpans()
    {
        await using var s=await ObservabilityScenario.CreateAsync();await s.PublishAsync("A");
        var red=System.Environment.GetEnvironmentVariable("WEBAPI_OBS_EXPECT_EMPTY")=="true";var trace=Guid.NewGuid().ToString("N");var start=DateTimeOffset.UtcNow.AddSeconds(-2);var before=await s.RawCounterAsync();
        if(!red)for(var node=0;node<2;node++)for(var n=0;n<4;n++){var r=await s.GatewayRequestAsync(node,trace:trace);Assert.Equal(200,r.Status);Assert.Equal("A",r.Body.GetProperty("backendId").GetString());}
        var counter=red?await s.RawCounterAsync():await ObservabilityScenario.WaitForConditionAsync(s.RawCounterAsync,x=>x>=before+8);
        Assert.Equal(8,counter-before);
        var logs=await ObservabilityScenario.WaitForConditionAsync(()=>s.QueryAsync("/observability/logs?"+s.Range(start,DateTimeOffset.UtcNow)+"&traceId="+trace),x=>x.GetProperty("data").GetProperty("items").GetArrayLength()==8);
        Assert.All(logs.GetProperty("data").GetProperty("items").EnumerateArray(),x=>Assert.Equal(s.EnvironmentId,x.GetProperty("environmentId").GetGuid()));
        var detail=await ObservabilityScenario.WaitForConditionAsync(()=>s.QueryAsync("/observability/traces/"+trace+"?"+s.Range(start,DateTimeOffset.UtcNow)),x=>x.GetProperty("data").GetProperty("spans").GetArrayLength()==16);
        var spans=detail.GetProperty("data").GetProperty("spans").EnumerateArray().ToArray();Assert.Contains(spans,x=>x.GetProperty("kind").GetString()=="Server");Assert.Contains(spans,x=>x.GetProperty("kind").GetString()=="Client");
        var plain=await s.GatewayRequestAsync(0);Assert.Equal(200,plain.Status);var requestId=plain.Body.GetProperty("traceId").GetString()!;
        var plainLogs=await ObservabilityScenario.WaitForConditionAsync(()=>s.QueryAsync("/observability/logs?"+s.Range(start,DateTimeOffset.UtcNow)+"&keyword="+Uri.EscapeDataString(requestId)),x=>x.GetProperty("data").GetProperty("items").GetArrayLength()==1);
        var plainTrace=plainLogs.GetProperty("data").GetProperty("items")[0].GetProperty("traceId").GetString()!;
        var plainDetail=await ObservabilityScenario.WaitForConditionAsync(()=>s.QueryAsync("/observability/traces/"+plainTrace+"?"+s.Range(start,DateTimeOffset.UtcNow)),x=>x.GetProperty("data").GetProperty("spans").GetArrayLength()==2);
        Assert.False(plainDetail.GetProperty("data").GetProperty("partialTrace").GetBoolean());
        Directory.CreateDirectory("/workspace/docs/evidence/observability");await File.WriteAllTextAsync("/workspace/docs/evidence/observability/loop.json",JsonSerializer.Serialize(new{complete=true,project=System.Environment.GetEnvironmentVariable("WEBAPI_E2E_PROJECT"),source="actual-two-gateways-collector-prometheus-loki-tempo",counterDelta=counter-before,logCount=8,spanCount=16,ordinaryRequestSpanCount=2,ordinaryRequestTraceComplete=true,environmentId=s.EnvironmentId,traceId=trace},new JsonSerializerOptions{WriteIndented=true}));
    }
}

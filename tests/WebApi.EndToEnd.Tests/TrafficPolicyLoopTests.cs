using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;
public sealed class TrafficPolicyLoopTests
{
    [Fact,Trait("Scenario","Policies")] public async Task RealDoubleGatewayAndThreeSources()
    {
        var directory=System.Environment.GetEnvironmentVariable("WEBAPI_POLICY_RESULT_DIRECTORY");Assert.NotNull(directory);var result=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory!,"result.json"))).RootElement;
        foreach(var name in new[]{"SharedRateLimit","CurrentInstanceCapabilityGate","ActualCircuitRecovery","FrozenPolicyPartialRelease","TwoPointZeroAndTwoPointOneRollback","OldGenerationInFlight","RedisFailureModes","ObservationScopeAndSecretExclusion"})Assert.True(result.GetProperty("checks").EnumerateArray().Single(c=>c.GetProperty("name").GetString()==name).GetProperty("passed").GetBoolean(),name);
        Assert.Equal(2,result.GetProperty("finalRelease").GetProperty("targets").EnumerateArray().Count(t=>t.GetProperty("acknowledged").GetBoolean()));Assert.Equal(200,result.GetProperty("finalStatus").GetInt32());Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(directory!,"original.snapshot")),await File.ReadAllBytesAsync(Path.Combine(directory!,"rollback.snapshot")));
    }
}

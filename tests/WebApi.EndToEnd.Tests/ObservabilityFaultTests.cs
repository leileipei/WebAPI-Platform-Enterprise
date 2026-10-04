using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;
[Trait("Scenario","Observability")]
public sealed class ObservabilityFaultTests
{
    [Fact] public async Task ActualContainerFaultEvidenceContainsRecoveryAndCoreContinuity()
    {
        using var doc=JsonDocument.Parse(await File.ReadAllTextAsync("/workspace/docs/evidence/observability/faults.json"));var root=doc.RootElement;
        Assert.True(root.GetProperty("complete").GetBoolean());Assert.Equal(System.Environment.GetEnvironmentVariable("WEBAPI_E2E_PROJECT"),root.GetProperty("project").GetString());var checks=root.GetProperty("checks").EnumerateArray().ToArray();
        foreach(var name in new[]{"ActualApprovedRollbackSurvivesObservationStack","lokiStopAndRecoveryKeepProxyAndApprovedPublishing","tempoStopAndRecoveryKeepProxyAndApprovedPublishing","prometheusStopAndRecoveryKeepProxyAndApprovedPublishing","CollectorOutageQueueFullProxySuccessAndRealMiddleGap","SingleNodeCollectionExpiryAndActualProcessRecovery","ActualWorkerKillPgLeaseExpiresSecondWorkerTakesOver","ActualGatewayLkgRestartWithControlPlaneRedisOffline","OldARequestAcrossBSwitchKeepsActualADestinationAndTelemetry"})Assert.Contains(checks,c=>c.GetProperty("name").GetString()==name&&c.GetProperty("status").GetString()=="passed");
    }
}

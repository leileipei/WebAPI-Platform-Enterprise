using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;
public sealed class CoreLoopTests
{
    [Fact] public async Task RealApprovedReleaseChangesBothGatewayResponses()
    {
        await using var s=await ExternalScenario.ConnectAsync();var first=await s.PublishAsync("A");await s.AssertBothAsync("A",first.GetProperty("deploymentSequence").GetInt64());var second=await s.PublishAsync("B");await s.AssertBothAsync("B",second.GetProperty("deploymentSequence").GetInt64());
        var rollback=await s.RollbackRestoresAWithHigherSequence(second,first);var swap=await s.OldRequestCompletesDuringSwap();
        Directory.CreateDirectory("/workspace/docs/evidence/core-loop");
        await File.WriteAllTextAsync("/workspace/docs/evidence/core-loop/scenarios.json",JsonSerializer.Serialize(new{complete=true,architecture="Linux "+System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,checks=new[]{new{name="RealApprovedReleaseChangesBothGatewayResponses",firstConfig=first.GetProperty("targetConfigVersion").GetInt64(),firstSequence=first.GetProperty("deploymentSequence").GetInt64(),secondConfig=second.GetProperty("targetConfigVersion").GetInt64(),secondSequence=second.GetProperty("deploymentSequence").GetInt64()},new{name="RollbackRestoresAWithHigherSequence",firstConfig=rollback.GetProperty("targetConfigVersion").GetInt64(),firstSequence=rollback.GetProperty("deploymentSequence").GetInt64(),secondConfig=0L,secondSequence=0L},new{name="OldRequestCompletesDuringSwap",firstConfig=swap.GetProperty("targetConfigVersion").GetInt64(),firstSequence=swap.GetProperty("deploymentSequence").GetInt64(),secondConfig=0L,secondSequence=0L}}},new JsonSerializerOptions{WriteIndented=true}));
    }
}

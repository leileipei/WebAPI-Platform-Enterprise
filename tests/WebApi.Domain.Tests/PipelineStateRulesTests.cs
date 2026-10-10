using WebApi.Domain.Delivery.Pipelines;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class PipelineStateRulesTests
{
 [Theory][InlineData("Completed",true)][InlineData("Cancelled",true)][InlineData("Invalidated",true)][InlineData("Active",false)][InlineData("Paused",false)][InlineData("TimedOut",false)]
 public void RunTerminalStateIsExplicit(string status,bool terminal)=>Assert.Equal(terminal,PipelineStateRules.IsTerminal(status));
 [Theory][InlineData("AwaitingEvidence")][InlineData("AwaitingAcceptance")][InlineData("AwaitingMapping")][InlineData("AwaitingPrecheck")][InlineData("AwaitingApproval")][InlineData("ReadyToDeploy")][InlineData("AwaitingVerification")]
 public void OnlyActiveOpenWindowCanWrite(string stage){var now=DateTimeOffset.UtcNow;Assert.True(PipelineStateRules.CanWrite("Active",stage,now.AddMinutes(1),now));foreach(var status in new[]{"Paused","TimedOut","Cancelled","Invalidated","Completed"})Assert.False(PipelineStateRules.CanWrite(status,stage,now.AddMinutes(1),now));Assert.False(PipelineStateRules.CanWrite("Active",stage,now,now));Assert.False(PipelineStateRules.CanWrite("Active",stage,now.AddTicks(-1),now));}
 [Theory][InlineData("Pending")][InlineData("Deploying")][InlineData("Passed")][InlineData("Rejected")][InlineData("DeploymentFailed")][InlineData("VerificationFailed")][InlineData("TimedOut")][InlineData("Invalidated")][InlineData("Cancelled")][InlineData("Unknown")]
 public void ClosedOrUnactivatedStageCannotWrite(string stage){var now=DateTimeOffset.UtcNow;Assert.False(PipelineStateRules.CanWrite("Active",stage,now.AddMinutes(1),now));}
}

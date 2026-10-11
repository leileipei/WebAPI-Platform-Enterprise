namespace WebApi.Domain.Delivery.Pipelines;
public static class PipelineStateRules
{
 public static bool IsTerminal(string status)=>status is "Completed" or "Cancelled" or "Invalidated";
 public static bool CanWrite(string runStatus,string stageStatus,DateTimeOffset deadlineAt,DateTimeOffset now)=>
  runStatus=="Active"&&deadlineAt>now&&stageStatus is "AwaitingEvidence" or "AwaitingAcceptance" or "AwaitingMapping" or "AwaitingPrecheck" or "AwaitingApproval" or "ReadyToDeploy" or "AwaitingVerification";
}

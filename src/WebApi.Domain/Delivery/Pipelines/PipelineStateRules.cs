namespace WebApi.Domain.Delivery.Pipelines;
public static class PipelineStateRules
{
 public static IReadOnlyList<string> WritableStageStatuses { get; } = Array.AsReadOnly<string>(
  ["AwaitingEvidence","AwaitingAcceptance","AwaitingMapping","AwaitingPrecheck","AwaitingApproval","ReadyToDeploy","AwaitingVerification"]);
 public static bool IsTerminal(string status)=>status is "Completed" or "Cancelled" or "Invalidated";
 public static bool CanWrite(string runStatus,string stageStatus,DateTimeOffset deadlineAt,DateTimeOffset now)=>
  runStatus=="Active"&&deadlineAt>now&&WritableStageStatuses.Contains(stageStatus);
}

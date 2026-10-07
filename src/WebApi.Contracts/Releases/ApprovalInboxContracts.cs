namespace WebApi.Contracts.Releases;

public sealed record ApprovalEligibility(bool CanAct, int? CurrentStepOrder, string? ReasonCode);

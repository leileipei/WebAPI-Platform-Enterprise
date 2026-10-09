namespace WebApi.Contracts.Releases;
public sealed record SaveDeliveryPolicyRequest(Guid SourceEnvironmentId,Guid TargetEnvironmentId,string Mode,IReadOnlyList<string> RequiredTestTypes,int VerificationValidityMinutes);
public sealed record DeliveryPolicyDto(Guid? Id,Guid ProjectId,Guid? SourceEnvironmentId,Guid? TargetEnvironmentId,string Mode,IReadOnlyList<string> RequiredTestTypes,int VerificationValidityMinutes,long Revision);

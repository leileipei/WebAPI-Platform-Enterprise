using WebApi.Contracts.Releases;
namespace WebApi.Infrastructure.Delivery;
public sealed record DeliveryEvidenceProfile(IReadOnlyList<string> RequiredTypes,int ValidityMinutes,string? ProfileHash);
public sealed record PipelineGateBinding(Guid RunId,Guid StageId,Guid? FormalAttemptId,Guid? CurrentAttemptId,string DefinitionHash,string RootArtifactHash,Guid RunCreatedBy,PipelineApprovalProfile? Approval);
public sealed record DeliveryGateContext(string Origin,Guid ProjectId,Guid SourceEnvironmentId,Guid TargetEnvironmentId,long PolicyRevision,string Mode,DeliveryEvidenceProfile SourceEvidence,DeliveryEvidenceProfile TargetEvidence,PipelineGateBinding? Pipeline=null,Guid? PolicyId=null);

namespace WebApi.Contracts.Releases;
public sealed record PipelineStageDefinition(int Order, Guid EnvironmentId, IReadOnlyList<string> RequiredTestTypes,
    int EvidenceValidityMinutes=1440, int WaitTimeoutMinutes=1440, Guid? ApprovalFlowId=null);
public sealed record PipelineDefinition(string Name, string Description, IReadOnlyList<PipelineStageDefinition> Stages);
public sealed record PipelineApprovalProfile(Guid FlowId,long FlowRevision,IReadOnlyList<ApprovalRule> Rules);
public sealed record PipelineStageProfile(int Order,Guid EnvironmentId,bool IsProduction,IReadOnlyList<string> RequiredTypes,
    int EvidenceValidityMinutes,int WaitTimeoutMinutes,PipelineApprovalProfile? Approval);
public sealed record PipelineVersionContent(PipelineDefinition Definition,IReadOnlyList<PipelineStageProfile> Profiles);
public sealed record CreatePipelineRunRequest(Guid PipelineVersionId,Guid RootArtifactId);
public sealed record ActivatePipelineRequest(Guid PipelineVersionId);
public sealed record RestoreDeliveryPolicyRequest(SaveDeliveryPolicyRequest Connection);
public sealed record PipelineStageVerificationRequest(string ExpectedContextHash,RecordVerificationRequest Evidence);
public sealed record PipelineAcceptanceRequest(string ExpectedContextHash,IReadOnlyList<Guid> VerificationIds);
public sealed record PipelineActionRequest(string Comment="");

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
public sealed record PipelineDto(Guid Id,Guid ProjectId,string Name,string Description,long Revision,string Status,string DefinitionVisibility,
    PipelineDefinition? Draft,PipelineVersionDto? LatestVersion=null);
public sealed record PipelineVersionDto(Guid Id,Guid PipelineId,int VersionNo,string DefinitionHash,PipelineVersionContent? Content,
    DateTimeOffset CreatedAt,Guid CreatedBy);
public sealed record PipelinePageDto<T>(IReadOnlyList<T> Items,int? Total,int Page,int Size,string Coverage);

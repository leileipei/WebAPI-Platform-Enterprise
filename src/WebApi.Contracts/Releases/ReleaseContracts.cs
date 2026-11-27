using WebApi.Contracts.Comparisons;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Runtime;
using System.Text.Json.Serialization;
namespace WebApi.Contracts.Releases;
public sealed record ResourceRevision(string Type,Guid Id,long Revision);
public sealed record CreateReleaseRequest(long BaseConfigVersion,IReadOnlyList<Guid> VersionIds,IReadOnlyList<ResourceRevision> ResourceRevisions,IReadOnlyList<Guid>? RiskReviewIds=null);
public sealed record PreviewReleaseRequest(long BaseConfigVersion,IReadOnlyList<Guid> VersionIds);
public sealed record RefreshReleasePreconditionsRequest(IReadOnlyList<ResourceRevision> ResourceRevisions);
public sealed record ApprovalActionRequest(string Comment="",int? ExpectedStepOrder=null,string? ExpectedCandidateHash=null);
public sealed record CreateRollbackRequest(long TargetConfigVersion);
public sealed record ApprovalRule(int StepOrder,string RoleCode,int RequiredCount);
public sealed record SaveApprovalFlowRequest(string Name,IReadOnlyList<ApprovalRule> Steps,bool Enabled=true);
public sealed record ApprovalFlowDto(Guid Id,Guid OrganizationId,string Name,bool Enabled,long Revision,IReadOnlyList<ApprovalRule> Steps);
public sealed record FrozenApiVersion(ApiDto Api,VersionDto Version,IReadOnlyList<ParameterDto> Parameters,IReadOnlyList<SchemaDto> Schemas);
public sealed record FrozenPolicy(Guid Id,string Type,string Config,bool Enabled,long Revision,Guid? SourcePolicyId=null);
public sealed record FrozenBinding(Guid RouteId,Guid PolicyId,int Priority);
public sealed record FrozenCredential(Guid Id,string AccessKey,string SecretHash,string SecretLast4,string Status,DateTimeOffset ValidFrom,DateTimeOffset ExpiresAt,long Revision,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Guid? EnvironmentId=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? BindingMode=null);
public sealed record FrozenApplication(ApplicationDto Application,IReadOnlyList<FrozenCredential> Credentials,IReadOnlyList<ApplicationPermissionDto> Permissions);
public sealed record FrozenGovernanceRoute(Guid RouteId,long AuthorizationRevision);
public sealed record FrozenGovernancePermission(Guid PermissionId,string Mode,IReadOnlyList<RuntimeRouteMethodGrant> RouteMethods);
public sealed record FrozenGovernanceTarget(Guid NodeId,string InstanceId,IReadOnlyList<string> SupportedSnapshotSchemas);
public sealed record FrozenGovernance24(Guid SecurityGeneration,long Epoch,string HeadHash,bool RequireEnvironmentCredentials,string MinimumSnapshotSchema,
    IReadOnlyList<FrozenGovernanceRoute> Routes,IReadOnlyList<FrozenGovernancePermission> Permissions,IReadOnlyList<FrozenGovernanceTarget> Targets);
public sealed record GovernedRollbackRouteImpact(Guid RouteId, bool ClusterChanged, bool TimeoutChanged, bool PoliciesChanged);
public sealed record FrozenGovernedRollback(long SourceConfigVersion, string SourceSnapshotHash, long BaselineDeploymentSequence,
    IReadOnlyList<GovernedRollbackRouteImpact> ChangedRoutes);
public sealed record FrozenReleaseCandidate(Guid EnvironmentId,Guid OrganizationId,Guid ProjectId,long BaselineConfigVersion,IReadOnlyList<Guid> VersionIds,IReadOnlyList<FrozenApiVersion> Versions,IReadOnlyList<RouteDto> Routes,IReadOnlyList<ClusterDto> Clusters,IReadOnlyList<FrozenPolicy> Policies,IReadOnlyList<FrozenBinding> Bindings,IReadOnlyList<FrozenApplication> Applications,IReadOnlyList<ResourceRevision> ResourceRevisions,IReadOnlyList<FrozenRiskReviewReference>? RiskReviewReferences=null,IReadOnlyList<SharedCredentialImpact>? SharedCredentialImpact=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FrozenGovernance24? Governance=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FrozenGovernedRollback? GovernedRollback=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] bool PreserveUnmappedPromotionRoutes=false);
public sealed record FrozenApplicationView(ApplicationDto Application,IReadOnlyList<CredentialDto> Credentials,IReadOnlyList<ApplicationPermissionDto> Permissions);
public sealed record FrozenCandidateView(IReadOnlyList<FrozenApiVersion> Versions,IReadOnlyList<RouteDto> Routes,IReadOnlyList<ClusterDto> Clusters,IReadOnlyList<FrozenApplicationView> Applications,IReadOnlyList<ResourceRevision> ResourceRevisions,IReadOnlyList<FrozenPolicy>? Policies=null,IReadOnlyList<FrozenBinding>? Bindings=null,bool? PreconditionsCurrent=null,IReadOnlyList<RiskReviewSummaryDto>? RiskReviews=null,IReadOnlyList<SharedCredentialImpact>? SharedCredentialImpact=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FrozenGovernedRollback? GovernedRollback=null);
public sealed record ApprovalTaskDto(Guid Id,int StepOrder,string RoleCode,string Status,Guid? ActorId,string? Comment,DateTimeOffset? ActedAt);
public sealed record ReleaseTargetDto(Guid NodeId,string InstanceId,bool Acknowledged,long? ConfigVersion,long? DeploymentSequence,string? ErrorCode);
public sealed record ReleaseDto(Guid Id,Guid EnvironmentId,string ReleaseNo,string State,string ReleaseType,Guid RequestedBy,DateTimeOffset CreatedAt,long BaselineConfigVersion,long TargetConfigVersion,long? DeploymentSequence,Guid? RollbackOf,FrozenCandidateView? FrozenCandidate,string? CandidateHash,IReadOnlyList<ApprovalTaskDto> ApprovalSteps,IReadOnlyList<ReleaseTargetDto> Targets,IReadOnlyList<string> Errors,Guid? RecoveryOf=null,ApprovalEligibility? ApprovalEligibility=null,ReleaseAccessContextDto? AccessContext=null,string? CurrentPublicOrigin=null,bool AccessAddressChanged=false,string CurrentBasePath="/",ApprovalDeliverySummaryDto? Delivery=null,string? ApplicantDisplayName=null);

public sealed record ReleaseAccessContextDto(Guid ReleaseId,Guid EnvironmentId,long AccessAddressRevision,string? PublicOrigin,string BasePath,DateTimeOffset CapturedAt,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? InternalOrigin=null,Guid? RecoveryOf=null);

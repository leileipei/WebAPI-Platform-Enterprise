namespace WebApi.Contracts.Releases;
public sealed record SaveDeliveryPolicyRequest(Guid SourceEnvironmentId,Guid TargetEnvironmentId,string Mode,IReadOnlyList<string> RequiredTestTypes,int VerificationValidityMinutes);
public sealed record DeliveryPolicyDto(Guid? Id,Guid ProjectId,Guid? SourceEnvironmentId,Guid? TargetEnvironmentId,string Mode,IReadOnlyList<string> RequiredTestTypes,int VerificationValidityMinutes,long Revision);
public sealed record ArtifactPolicyTemplate(string Type,string FrozenConfig,IReadOnlyList<string> EnvironmentFields,int Priority=0);
public sealed record ArtifactRoute(string Key,Guid ApiId,Guid VersionId,string Path,IReadOnlyList<string> Methods,int Priority,bool Enabled,int? TimeoutTemplate,IReadOnlyList<ArtifactPolicyTemplate> Policies,string AuthenticationMode="ApiKey");
public sealed record ArtifactApiContract(Guid ApiId,Guid VersionId,string Version,long SourceRevision,IReadOnlyList<WebApi.Contracts.Catalog.ParameterDto> Parameters,IReadOnlyList<WebApi.Contracts.Catalog.SchemaDto> Schemas);
public sealed record ArtifactContent(IReadOnlyList<ArtifactApiContract> Apis,IReadOnlyList<ArtifactRoute> Routes);
public sealed record ReleaseArtifactDto(Guid Id,Guid OrganizationId,Guid ProjectId,Guid SourceEnvironmentId,Guid SourceReleaseId,string ArtifactHash,string SourceSnapshotHash,Guid CreatedBy,DateTimeOffset CreatedAt,ArtifactContent Content);

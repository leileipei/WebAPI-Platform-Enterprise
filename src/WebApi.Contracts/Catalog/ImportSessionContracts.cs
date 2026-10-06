using WebApi.Contracts.OpenApi;
namespace WebApi.Contracts.Catalog;
public sealed record ImportSourceFile(string Name,string Content,string? Format=null);
public sealed record CreateImportPreviewRequest(Guid ProjectId,Guid EnvironmentId,Guid ClusterId,string? SourceText=null,string? SourceUrl=null,string? Format=null,IReadOnlyList<ImportSourceFile>? Files=null);
public sealed record ImportSessionDto(Guid PreviewId,string SourceHash,string BundleHash,string Dialect,DateTimeOffset ExpiresAt,long SourcePolicyRevision,IReadOnlyList<ImportOperationDto> Operations,IReadOnlyList<ContractIssue> Issues,string SourceFormat="json");
public sealed record CommitImportSessionRequest(string ExpectedBundleHash,IReadOnlyList<ImportTarget> Targets);

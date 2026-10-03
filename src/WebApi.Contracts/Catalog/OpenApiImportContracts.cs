namespace WebApi.Contracts.Catalog;
public sealed record ImportPreviewRequest(Guid ProjectId,Guid EnvironmentId,Guid ClusterId,string Source);
public sealed record ImportTarget(string OperationId,string? NewApiCode=null,string? NewApiName=null,Guid? ApiId=null,Guid? ExistingVersionId=null,string Version="1.0.0",long? ExpectedRevision=null,Guid? ExistingRouteId=null,long? ExpectedRouteRevision=null);
public sealed record ImportCommitRequest(ImportPreviewRequest Input,IReadOnlyList<ImportTarget> Targets);
public sealed record ImportOperationDto(string OperationId,string Method,string Path,string Summary,string SuggestedCode,bool Supported,IReadOnlyList<string> Warnings,int ParameterCount,int SchemaCount);
public sealed record ImportPreviewResponse(IReadOnlyList<ImportOperationDto> Operations,IReadOnlyList<string> Warnings);
public sealed record ImportedOperationDto(string OperationId,Guid ApiId,Guid VersionId,Guid RouteId);
public sealed record ImportCommitResponse(Guid ImportId,IReadOnlyList<ImportedOperationDto> Operations,IReadOnlyList<string> Warnings);

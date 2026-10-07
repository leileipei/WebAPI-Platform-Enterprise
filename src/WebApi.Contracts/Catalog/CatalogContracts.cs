namespace WebApi.Contracts.Catalog;
public sealed record SaveApiRequest(string Code,string Name,string? Description=null,Guid? GroupId=null,Guid? OwnerUserId=null,string LifecycleStatus="Draft");
public sealed record ApiDto(Guid Id,Guid OrganizationId,Guid ProjectId,Guid? GroupId,string Code,string Name,string? Description,string LifecycleStatus,Guid OwnerUserId,long WorkingRevision);
public sealed record ApiDetailDto(ApiDto Api,long WorkingRevision,IReadOnlyDictionary<Guid,long?> RunningConfigVersion,Guid? PendingReleaseId,IReadOnlyList<VersionDto> Versions);
public sealed record CreateVersionRequest(string Version,string ChangeType="compatible",string? OpenapiDocument=null,string? OpenapiSource=null,string? SourceFormat=null,string? Dialect=null);
public sealed record VersionDto(Guid Id,Guid ApiId,string Version,string Status,string ChangeType,string? OpenapiDocument,string? OpenapiSource,string? SourceFormat,string? SchemaHash,Guid CreatedBy,DateTimeOffset CreatedAt,DateTimeOffset? SealedAt,long Revision,[property: System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Dialect=null);
public sealed record SaveGroupRequest(string Name,Guid? ParentId=null,int SortOrder=0);
public sealed record GroupDto(Guid Id,Guid ProjectId,string Name,Guid? ParentId,int SortOrder,long Revision);
public sealed record SaveParameterRequest(Guid? Id,string Location,string Name,string DataType,bool Required,string? Schema=null,string? Description=null,string? ExampleJson=null);
public sealed record ContractExampleOption(string Name,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Json=null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ExternalValue=null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? UnverifiedReason=null);
public sealed record ParameterDto(Guid Id,Guid ApiVersionId,string Location,string Name,string DataType,bool Required,string? Schema,string? Description,string? ExampleJson)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ContractExampleOption>? Examples {get;init;}
}
public sealed record SaveSchemaRequest(Guid? Id,string SchemaType,string Name,int? StatusCode,string ContentType,string SchemaJson,string? ExampleJson=null);
public sealed record SchemaDto(Guid Id,Guid ApiVersionId,string SchemaType,string Name,int? StatusCode,string ContentType,string SchemaJson,string? SchemaHash,string? ExampleJson)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ContractExampleOption>? Examples {get;init;}
}
public sealed record SaveRouteRequest(Guid? Id,Guid ApiVersionId,string RouteName,string Path,IReadOnlyList<string> Methods,Guid ClusterId,int Priority=100,bool Enabled=true,int? TimeoutMs=null,bool RequireApiKey=true,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? AuthenticationChange=null);
public sealed record RouteDto(Guid Id,Guid ApiVersionId,Guid EnvironmentId,string RouteName,string Path,string NormalizedPath,IReadOnlyList<string> Methods,Guid ClusterId,int Priority,int MatchOrder,bool Enabled,int TimeoutMs,bool RequireApiKey,long Revision,int? EffectiveTimeoutMs=null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? EffectiveAuthenticationMode=null);
public sealed record SaveClusterRequest(string Name,string LoadBalancingPolicy="RoundRobin",bool HealthCheckEnabled=false,string HealthCheckPath="/health",int HealthCheckIntervalSec=30,string Status="Active");
public sealed record ClusterDto(Guid Id,Guid ProjectId,Guid EnvironmentId,string Name,string LoadBalancingPolicy,bool HealthCheckEnabled,string HealthCheckPath,int HealthCheckIntervalSec,string Status,long Revision,IReadOnlyList<DestinationDto> Destinations);
public sealed record SaveDestinationRequest(string Name,string Address,int Weight=1,bool Enabled=true,string? Metadata=null);
public sealed record DestinationDto(Guid Id,Guid ClusterId,string Name,string Address,int Weight,bool Enabled,string? Metadata,long Revision);

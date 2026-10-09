namespace WebApi.Contracts.Governance;
public sealed record EnvironmentAccessSettings(string? PublicOrigin,string? InternalOrigin,string BasePath);
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AccessView>))]
public enum AccessView { Working,Running }
public sealed record RouteAccessDto(Guid RouteId,string Method,string PathTemplate,string? PublicTemplate,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? InternalTemplate,long? RunningConfigVersion);
public sealed record EnvironmentApiAccessDto(Guid EnvironmentId,long AccessAddressRevision,bool Configured,AccessView View,IReadOnlyList<RouteAccessDto> Routes);

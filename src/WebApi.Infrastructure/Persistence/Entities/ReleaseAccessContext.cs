namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleaseAccessContext
{
    public Guid ReleaseId {get;init;}
    public Guid EnvironmentId {get;init;}
    public long AccessAddressRevision {get;init;}
    public string? PublicOrigin {get;init;}
    public string? InternalOrigin {get;init;}
    public string BasePath {get;init;}="/";
    public DateTimeOffset CapturedAt {get;init;}=DateTimeOffset.UtcNow;
}

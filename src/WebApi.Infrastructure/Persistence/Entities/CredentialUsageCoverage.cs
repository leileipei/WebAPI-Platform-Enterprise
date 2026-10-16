namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class CredentialUsageCoverage
{
    public Guid EnvironmentId {get;set;}
    public Guid NodeId {get;set;}
    public DateTimeOffset HourStart {get;set;}
    public string Intervals {get;set;}="[]";
    public string Gaps {get;set;}="[]";
    public DateTimeOffset? DurableThrough {get;set;}
    public DateTimeOffset? AcceptedThrough {get;set;}
    public string State {get;set;}="Partial";
    public DateTimeOffset UpdatedAt {get;set;}=DateTimeOffset.UtcNow;
}

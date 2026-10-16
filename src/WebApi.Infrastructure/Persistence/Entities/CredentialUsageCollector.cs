namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class CredentialUsageCollector
{
    public Guid NodeId {get;set;}
    public Guid CollectorId {get;set;}
    public Guid EnvironmentId {get;set;}
    public string Protocol {get;set;}="1.0";
    public long AcceptedSequence {get;set;}
    public DateTimeOffset? AcceptedThrough {get;set;}
    public DateTimeOffset CreatedAt {get;set;}=DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt {get;set;}
}

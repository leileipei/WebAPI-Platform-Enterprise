namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class CredentialUsageBatch
{
    public Guid NodeId {get;set;}
    public Guid CollectorId {get;set;}
    public long Sequence {get;set;}
    public string CoreHash {get;set;}=string.Empty;
    public DateTimeOffset AppliedAt {get;set;}=DateTimeOffset.UtcNow;
    public DateTimeOffset? AcceptedThrough {get;set;}
}

namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class CredentialUsageCollectorInstance
{
    public Guid NodeId {get;set;}
    public Guid CollectorId {get;set;}
    public Guid InstanceId {get;set;}
    public DateTimeOffset RegisteredAt {get;set;}=DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt {get;set;}
    public string IdentityProof {get;set;}="current";
}

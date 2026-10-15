namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class CredentialUsageHourly
{
    public Guid EnvironmentId {get;set;}
    public Guid CredentialId {get;set;}
    public DateTimeOffset HourStart {get;set;}
    public Guid ApplicationId {get;set;}
    public long Count {get;set;}
    public DateTimeOffset LastUsedAt {get;set;}
}

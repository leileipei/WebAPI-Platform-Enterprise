namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class NotificationChannelProfile
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Channel {get;set;}=string.Empty;
    public string ConfigurationHash {get;set;}=string.Empty;
    public string PrivateConfiguration {get;set;}=string.Empty;
    public string ProtectedSecretFingerprint {get;set;}=string.Empty;
    public Guid CreatedBy {get;set;}
    public DateTimeOffset CreatedAt {get;set;}=DateTimeOffset.UtcNow;
    public long SettingsRevision {get;set;}
}

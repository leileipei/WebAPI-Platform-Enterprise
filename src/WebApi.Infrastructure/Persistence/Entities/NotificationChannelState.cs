namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class NotificationChannelState
{
    public string Channel {get;set;}=string.Empty;
    public Guid? ProfileId {get;set;}
    public bool Enabled {get;set;}
    public long Revision {get;set;}=1;
}

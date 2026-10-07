namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class NotificationDeliveryAttempt
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public Guid DeliveryId {get;set;}
    public int AttemptNo {get;set;}
    public long LeaseToken {get;set;}
    public DateTimeOffset StartedAt {get;set;}
    public DateTimeOffset? CompletedAt {get;set;}
    public string? Outcome {get;set;}
    public string? Code {get;set;}
    public int? ProtocolStatus {get;set;}
}

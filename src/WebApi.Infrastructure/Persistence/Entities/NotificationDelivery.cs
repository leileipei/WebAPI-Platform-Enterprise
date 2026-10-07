namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class NotificationDelivery
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Kind {get;set;}="Alert";
    public Guid? EventId {get;set;}
    public Guid? TransitionId {get;set;}
    public Guid? TriggeredDeliveryId {get;set;}
    public Guid? OrganizationId {get;set;}
    public Guid? ProjectId {get;set;}
    public Guid? EnvironmentId {get;set;}
    public Guid? CreatedBy {get;set;}
    public string Channel {get;set;}=string.Empty;
    public string Target {get;set;}=string.Empty;
    public string TargetHash {get;set;}=string.Empty;
    public Guid? ProfileId {get;set;}
    public byte[] Payload {get;set;}=[];
    public int MaxAttempts {get;set;}=5;
    public int BaseDelaySeconds {get;set;}=30;
    public int MaxDelaySeconds {get;set;}=900;
    public int ExpiresAfterMinutes {get;set;}=1440;
    public string Status {get;set;}="Queued";
    public string? Reason {get;set;}
    public int AttemptCount {get;set;}
    public DateTimeOffset? NextAttemptAt {get;set;}
    public string? LeaseOwner {get;set;}
    public long LeaseToken {get;set;}
    public DateTimeOffset? LeaseUntil {get;set;}
    public long Revision {get;set;}=1;
    public DateTimeOffset CreatedAt {get;set;}=DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt {get;set;}
    public DateTimeOffset? CompletedAt {get;set;}
}

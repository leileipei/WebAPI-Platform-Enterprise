namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class GatewayNodeEvent
{
    public long Id { get; set; }
    public Guid GatewayNodeId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Detail { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

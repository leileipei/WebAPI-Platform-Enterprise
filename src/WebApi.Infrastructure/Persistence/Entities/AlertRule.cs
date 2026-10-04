namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public string Severity { get; set; } = "Warning";
    public bool Enabled { get; set; } = true;
    public int ForSeconds { get; set; }
    public string TargetType { get; set; } = "Environment";
    public Guid? TargetId { get; set; }
    public int WindowSeconds { get; set; } = 300;
    public string Notification { get; set; } = "{\"inConsole\":true,\"requestedChannels\":[]}";
    public long Revision { get; set; } = 1;
    public long LogicRevision { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

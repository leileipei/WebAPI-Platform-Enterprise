namespace WebApi.Infrastructure.Persistence.Entities;

/// <summary>Records the observed database and schema shape before governance migration.</summary>
public sealed class EnvironmentSecuritySourceReceipt
{
    public Guid EnvironmentId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OrganizationId { get; set; }
    public string SourceDatabase { get; set; } = "";
    public long SourceDatabaseOid { get; set; }
    public string SourceClusterId { get; set; } = "";
    public string SourceMigration { get; set; } = "";
    public string SourceSchemaHash { get; set; } = "";
    public string SourceHistoryHash { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
}

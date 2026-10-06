namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ProjectImportSourcePolicy
{
    public Guid ProjectId { get; set; }
    public string RulesJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

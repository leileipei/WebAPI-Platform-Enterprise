namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApprovalFlow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ScopeType { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public long Revision { get; set; } = 1;
}

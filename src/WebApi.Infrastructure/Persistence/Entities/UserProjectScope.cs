namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class UserProjectScope
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public string AccessMode { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
}

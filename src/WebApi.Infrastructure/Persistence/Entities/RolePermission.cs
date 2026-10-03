namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
}

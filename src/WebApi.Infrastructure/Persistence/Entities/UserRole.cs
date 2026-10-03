namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

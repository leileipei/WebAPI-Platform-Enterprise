namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class UserRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    public string Status { get; set; } = "Active";
    public string AuthSource { get; set; } = "local";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string SecurityStamp { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
}

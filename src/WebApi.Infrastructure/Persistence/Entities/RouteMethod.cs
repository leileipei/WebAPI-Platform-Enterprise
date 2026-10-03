namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class RouteMethod
{
    public Guid RouteId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string Method { get; set; } = string.Empty;
    public string NormalizedPath { get; set; } = string.Empty;
}

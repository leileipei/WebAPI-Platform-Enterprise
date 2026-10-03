namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class RoutePolicyBinding
{
    public Guid RouteId { get; set; }
    public Guid PolicyId { get; set; }
    public int Priority { get; set; }
}

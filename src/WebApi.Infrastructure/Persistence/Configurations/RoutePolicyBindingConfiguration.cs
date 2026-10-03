using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class RoutePolicyBindingConfiguration : IEntityTypeConfiguration<RoutePolicyBinding>
{
    public void Configure(EntityTypeBuilder<RoutePolicyBinding> b)
    {
        b.ToTable("route_policy_bindings"); b.HasKey(x => new { x.RouteId, x.PolicyId });
        b.Property(x => x.RouteId).HasColumnName("route_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.PolicyId).HasColumnName("policy_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Priority).HasColumnName("priority").HasColumnType("int").IsRequired();
    }
}

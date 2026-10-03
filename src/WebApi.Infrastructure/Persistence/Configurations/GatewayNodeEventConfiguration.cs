using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class GatewayNodeEventConfiguration : IEntityTypeConfiguration<GatewayNodeEvent>
{
    public void Configure(EntityTypeBuilder<GatewayNodeEvent> b)
    {
        b.ToTable("gateway_node_events"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("bigint").IsRequired().UseIdentityByDefaultColumn();
        b.Property(x => x.GatewayNodeId).HasColumnName("gateway_node_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.EventType).HasColumnName("event_type").HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.Message).HasColumnName("message").HasColumnType("text").IsRequired();
        b.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
    }
}

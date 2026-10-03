using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency_records"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.ActorId).HasColumnName("actor_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.ScopeKey).HasColumnName("scope_key").HasColumnType("varchar(256)").IsRequired();
        b.Property(x => x.Operation).HasColumnName("operation").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.Key).HasColumnName("key").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.RequestHash).HasColumnName("request_hash").HasColumnType("varchar(128)").IsRequired();
        b.Property(x => x.ResponseBytes).HasColumnName("response_bytes").HasColumnType("bytea");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
    }
}

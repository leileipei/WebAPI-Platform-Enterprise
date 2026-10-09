using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Persistence.Configurations;
public sealed class ReleasePromotionEventConfiguration : IEntityTypeConfiguration<ReleasePromotionEvent>
{
    public void Configure(EntityTypeBuilder<ReleasePromotionEvent> b)
    {
        b.ToTable("release_promotion_events");b.HasKey(x=>x.Id);
        b.Property(x=>x.Id).HasColumnName("id").HasColumnType("uuid");
        b.Property(x=>x.OrganizationId).HasColumnName("organization_id").HasColumnType("uuid");
        b.Property(x=>x.ProjectId).HasColumnName("project_id").HasColumnType("uuid");
        b.Property(x=>x.PromotionId).HasColumnName("promotion_id").HasColumnType("uuid");
        b.Property(x=>x.AcceptanceId).HasColumnName("acceptance_id").HasColumnType("uuid");
        b.Property(x=>x.EnvironmentId).HasColumnName("environment_id").HasColumnType("uuid");
        b.Property(x=>x.Phase).HasColumnName("phase").HasColumnType("varchar(32)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.FromStatus).HasColumnName("from_status").HasColumnType("varchar(32)");
        b.Property(x=>x.ToStatus).HasColumnName("to_status").HasColumnType("varchar(32)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.ReasonCode).HasColumnName("reason_code").HasColumnType("varchar(128)").IsRequired().HasDefaultValue(string.Empty);
        b.Property(x=>x.ActorId).HasColumnName("actor_id").HasColumnType("uuid");
        b.Property(x=>x.ReleaseId).HasColumnName("release_id").HasColumnType("uuid");
        b.Property(x=>x.VerificationId).HasColumnName("verification_id").HasColumnType("uuid");
        b.Property(x=>x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
        b.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.OrganizationId}).HasPrincipalKey(x=>new{x.Id,x.OrganizationId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleasePromotion>().WithMany().HasForeignKey(x=>new{x.PromotionId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.TargetEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseTestAcceptance>().WithMany().HasForeignKey(x=>new{x.AcceptanceId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.SourceEnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseRecord>().WithMany().HasForeignKey(x=>new{x.ReleaseId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReleaseVerification>().WithMany().HasForeignKey(x=>new{x.VerificationId,x.ProjectId,x.EnvironmentId}).HasPrincipalKey(x=>new{x.Id,x.ProjectId,x.EnvironmentId}).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserRecord>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t=>t.HasCheckConstraint("ck_delivery_event_owner", "(promotion_id IS NOT NULL)::int + (acceptance_id IS NOT NULL)::int = 1"));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Persistence.Migrations;

namespace WebApi.Infrastructure.Persistence.Configurations;

public sealed class FormalRotationDecisionConfiguration : IEntityTypeConfiguration<FormalRotationDecision>
{
    public void Configure(EntityTypeBuilder<FormalRotationDecision> builder)
    {
        builder.ToTable("formal_rotation_decisions", "public", table =>
            table.HasCheckConstraint("ck_formal_rotation_decision", FormalRotationDecisionProjection.DecisionConstraint));
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(row => row.ScheduleId).HasColumnName("schedule_id");
        builder.Property(row => row.Kind).HasColumnName("kind").UseCollation("C").IsRequired();
        builder.Property(row => row.RecordedAt).HasColumnName("recorded_at");
        builder.Property(row => row.IntentHash).HasColumnName("intent_hash").IsRequired();

        builder.HasOne<FormalCredentialRotation>().WithMany().HasForeignKey(row => row.ScheduleId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_formal_rotation_decisions_schedule");
        builder.HasIndex(row => new { row.ScheduleId, row.Kind }).IsUnique()
            .HasDatabaseName("IX_formal_rotation_decisions_schedule_id_kind");
        builder.HasIndex(row => row.ScheduleId).IsUnique().HasFilter(FormalRotationDecisionProjection.CompetingKindFilter)
            .HasDatabaseName("IX_formal_rotation_decisions_schedule_complete_or_abort");
    }
}

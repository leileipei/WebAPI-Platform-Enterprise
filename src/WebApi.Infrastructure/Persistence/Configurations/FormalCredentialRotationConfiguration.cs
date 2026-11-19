using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Persistence.Migrations;

namespace WebApi.Infrastructure.Persistence.Configurations;

public sealed class FormalCredentialRotationConfiguration : IEntityTypeConfiguration<FormalCredentialRotation>
{
    public void Configure(EntityTypeBuilder<FormalCredentialRotation> builder)
    {
        builder.ToTable("formal_credential_rotations", table =>
            table.HasCheckConstraint("ck_formal_credential_rotation", FormalCredentialRotationSchedule.ScheduleConstraint));
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(row => row.CredentialId).HasColumnName("credential_id");
        builder.Property(row => row.ReplacementCredentialId).HasColumnName("replacement_credential_id");
        builder.Property(row => row.EnvironmentId).HasColumnName("environment_id");
        builder.Property(row => row.HardDeadline).HasColumnName("hard_deadline");
        builder.Property(row => row.RecordedAt).HasColumnName("recorded_at");
        builder.Property(row => row.IntentHash).HasColumnName("intent_hash").HasMaxLength(64).IsRequired();

        builder.HasOne<ApplicationCredential>().WithMany().HasForeignKey(row => row.CredentialId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_formal_credential_rotations_old_credential");
        builder.HasOne<ApplicationCredential>().WithMany().HasForeignKey(row => row.ReplacementCredentialId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_formal_credential_rotations_replacement_credential");
        builder.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(row => row.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_formal_credential_rotations_environment");

        builder.HasIndex(row => row.CredentialId).HasDatabaseName("IX_formal_credential_rotations_credential_id");
        builder.HasIndex(row => row.ReplacementCredentialId).HasDatabaseName("IX_formal_credential_rotations_replacement_credential_id");
        builder.HasIndex(row => new { row.HardDeadline, row.Id }).HasDatabaseName("IX_formal_credential_rotations_hard_deadline_id");
        builder.HasIndex(row => new { row.EnvironmentId, row.HardDeadline }).HasDatabaseName("IX_formal_credential_rotations_environment_id_hard_deadline");
    }
}

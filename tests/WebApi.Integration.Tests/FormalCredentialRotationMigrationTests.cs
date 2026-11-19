using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Persistence.Migrations;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[Collection("MigrationIsolation")]
public sealed class FormalCredentialRotationMigrationTests
{
    private const string RotationMigration = "20261119140000_FormalCredentialRotation";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothUpgradePathsPreserveTheScheduleAndInstallTheDecisionProjection(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        FormalCredentialRotation scheduled;
        await using (var predecessor = database.Context())
        {
            await predecessor.GetService<IMigrator>().MigrateAsync(RotationMigration);
            Assert.Equal(GovernanceSchemaFingerprints.Expected[RotationMigration],
                await predecessor.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
            scheduled = await SeedScheduleAsync(predecessor);
            predecessor.Add(scheduled);
            await predecessor.SaveChangesAsync();
        }

        await InstallUpgradeAsync(database, sqlEntry);

        await using var db = database.Context();
        Assert.True(await db.Database.SqlQueryRaw<bool>("""
            SELECT pg_catalog.to_regclass('public.formal_rotation_decisions') IS NOT NULL AS "Value"
            """).SingleAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(FormalRotationDecisionProjection.Id, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(GovernanceSchemaFingerprints.Expected[FormalRotationDecisionProjection.Id],
            await db.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        Assert.Equal(scheduled.IntentHash, await db.Set<FormalCredentialRotation>()
            .Where(row => row.Id == scheduled.Id).Select(row => row.IntentHash).SingleAsync());
        Assert.Empty(await db.FormalRotationDecisions.ToArrayAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
                WHERE conrelid = 'public.formal_rotation_decisions'::regclass
                  AND confrelid = 'public.formal_credential_rotations'::regclass
                  AND contype = 'f' AND confdeltype = 'r') AS "Value"
            """).SingleAsync());

        var decision = new FormalRotationDecision
        {
            Id = Guid.NewGuid(), ScheduleId = scheduled.Id, Kind = "Complete",
            RecordedAt = scheduled.RecordedAt.AddMinutes(1), IntentHash = new string('c', 64)
        };
        db.FormalRotationDecisions.Add(decision);
        await db.SaveChangesAsync();

        await AssertImmutableAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.formal_rotation_decisions SET kind='AbortRequested' WHERE id={decision.Id}"));
        await AssertImmutableAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM public.formal_rotation_decisions WHERE id={decision.Id}"));
        await AssertImmutableAsync(() => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE public.formal_rotation_decisions"));
        var downgrade = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(RotationMigration));
        Assert.Equal(PostgresErrorCodes.CheckViolation, downgrade.SqlState);
        Assert.Equal("formal_rotation_decision_down_blocked", downgrade.MessageText);

        if (sqlEntry) await InstallUpgradeAsync(database, sqlEntry);
        Assert.Equal(FormalRotationDecisionProjection.Id, (await db.Database.GetAppliedMigrationsAsync()).Last());
        var retained = await db.FormalRotationDecisions.AsNoTracking().SingleAsync();
        Assert.Equal((decision.Id, decision.ScheduleId, decision.Kind, decision.RecordedAt, decision.IntentHash),
            (retained.Id, retained.ScheduleId, retained.Kind, retained.RecordedAt, retained.IntentHash));
        Assert.Equal(TimeSpan.Zero, retained.RecordedAt.Offset);
    }

    [Fact]
    public async Task DecisionConstraintsRejectConflictsAndInvalidEvidence()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var completed = await SeedScheduleAsync(db);
        db.Add(completed);
        await db.SaveChangesAsync();
        var aborted = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.formal_credential_rotations
                (id, credential_id, replacement_credential_id, environment_id, hard_deadline, recorded_at, intent_hash)
            SELECT {aborted}, credential_id, replacement_credential_id, environment_id, hard_deadline, recorded_at, intent_hash
            FROM public.formal_credential_rotations WHERE id={completed.Id}
            """);

        var at = completed.RecordedAt.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var hash = new string('c', 64);
        Task AppendAsync(Guid schedule, string kind, Guid? id = null, string? recorded = null, string? intent = null) =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public.formal_rotation_decisions (id, schedule_id, kind, recorded_at, intent_hash)
                VALUES ({id ?? Guid.NewGuid()}, {schedule}, {kind}, CAST({recorded ?? at} AS timestamptz), {intent ?? hash})
                """);

        await AssertConstraintAsync(() => AppendAsync(completed.Id, "Complete", id: Guid.Empty),
            PostgresErrorCodes.CheckViolation, "ck_formal_rotation_decision");
        await AssertConstraintAsync(() => AppendAsync(completed.Id, "complete"),
            PostgresErrorCodes.CheckViolation, "ck_formal_rotation_decision");
        await AssertConstraintAsync(() => AppendAsync(completed.Id, "Complete "),
            PostgresErrorCodes.CheckViolation, "ck_formal_rotation_decision");
        await AssertConstraintAsync(() => AppendAsync(aborted, "AbortRequested "),
            PostgresErrorCodes.CheckViolation, "ck_formal_rotation_decision");
        await AssertConstraintAsync(() => AppendAsync(completed.Id, "Complete", recorded: "infinity"),
            PostgresErrorCodes.CheckViolation, "ck_formal_rotation_decision");
        await AssertConstraintAsync(() => AppendAsync(completed.Id, "Complete", recorded: "-infinity"),
            PostgresErrorCodes.CheckViolation, "ck_formal_rotation_decision");
        await AssertConstraintAsync(() => AppendAsync(completed.Id, "Complete", intent: hash.ToUpperInvariant()),
            PostgresErrorCodes.CheckViolation, "ck_formal_rotation_decision");
        await AssertConstraintAsync(() => AppendAsync(completed.Id, "Complete", intent: hash[..63]),
            PostgresErrorCodes.CheckViolation, "ck_formal_rotation_decision");
        await AssertConstraintAsync(() => AppendAsync(completed.Id, "Complete", intent: hash + " "),
            PostgresErrorCodes.CheckViolation, "ck_formal_rotation_decision");
        await AssertConstraintAsync(() => AppendAsync(Guid.NewGuid(), "Complete"),
            PostgresErrorCodes.ForeignKeyViolation, "FK_formal_rotation_decisions_schedule");

        var operation = Guid.NewGuid();
        await AppendAsync(completed.Id, "Complete", id: operation);
        await AppendAsync(aborted, "AbortRequested");
        await AppendAsync(aborted, "AbortConfirmed");
        await AssertConstraintAsync(() => AppendAsync(completed.Id, "AbortConfirmed", id: operation),
            PostgresErrorCodes.UniqueViolation, "PK_formal_rotation_decisions");
        await AssertConstraintAsync(() => AppendAsync(aborted, "AbortConfirmed"),
            PostgresErrorCodes.UniqueViolation, "IX_formal_rotation_decisions_schedule_id_kind");
        await AssertConstraintAsync(() => AppendAsync(completed.Id, "AbortRequested"),
            PostgresErrorCodes.UniqueViolation, "IX_formal_rotation_decisions_schedule_complete_or_abort");
        await AssertConstraintAsync(() => AppendAsync(aborted, "Complete"),
            PostgresErrorCodes.UniqueViolation, "IX_formal_rotation_decisions_schedule_complete_or_abort");
        Assert.Equal(3, await db.FormalRotationDecisions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothUpgradePathsPreserveTheCredentialsAndCreateAnAppendOnlySchedule(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        FormalCredentialRotation scheduled;
        await using (var predecessor = database.Context())
        {
            await predecessor.GetService<IMigrator>().MigrateAsync(LegacyGlobalRevocationInheritance.Id);
            Assert.Equal(GovernanceSchemaFingerprints.Expected[LegacyGlobalRevocationInheritance.Id],
                await predecessor.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
            scheduled = await SeedScheduleAsync(predecessor);
        }
        await InstallUpgradeAsync(database, sqlEntry);

        await using var db = database.Context();
        Assert.True(await db.Database.SqlQueryRaw<bool>("""
            SELECT pg_catalog.to_regclass('public.formal_credential_rotations') IS NOT NULL AS "Value"
            """).SingleAsync());
        Assert.Equal(FormalRotationDecisionProjection.Id, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(GovernanceSchemaFingerprints.Expected[FormalRotationDecisionProjection.Id],
            await db.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        Assert.Equal(2, await db.Set<ApplicationCredential>().CountAsync(key => key.EnvironmentId == scheduled.EnvironmentId));
        db.Add(scheduled);
        await db.SaveChangesAsync();
        var persisted = await db.Set<FormalCredentialRotation>().AsNoTracking().SingleAsync();
        Assert.Equal((scheduled.Id, scheduled.CredentialId, scheduled.ReplacementCredentialId, scheduled.EnvironmentId,
                scheduled.HardDeadline, scheduled.RecordedAt, scheduled.IntentHash),
            (persisted.Id, persisted.CredentialId, persisted.ReplacementCredentialId, persisted.EnvironmentId,
                persisted.HardDeadline, persisted.RecordedAt, persisted.IntentHash));
        Assert.True(await db.Database.SqlQueryRaw<bool>("""
            SELECT (SELECT count(*) FROM pg_catalog.pg_constraint WHERE conrelid='public.formal_credential_rotations'::regclass
                      AND contype='f' AND confdeltype='r')=3
                AND EXISTS (SELECT 1 FROM pg_catalog.pg_index WHERE indrelid='public.formal_credential_rotations'::regclass
                        AND indexrelid='public."IX_formal_credential_rotations_credential_id"'::regclass AND NOT indisunique) AS "Value"
            """).SingleAsync());

        await AssertImmutableAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.formal_credential_rotations SET hard_deadline=hard_deadline + interval '1 second' WHERE id={scheduled.Id}"));
        await AssertImmutableAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM public.formal_credential_rotations WHERE id={scheduled.Id}"));
        await AssertImmutableAsync(() => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE public.formal_credential_rotations CASCADE"));
    }

    [Fact]
    public async Task TheScheduleMigrationCannotBeRolledBack()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.GetService<IMigrator>().MigrateAsync(RotationMigration);
        var scheduled = await SeedScheduleAsync(db);
        db.Add(scheduled);
        await db.SaveChangesAsync();

        await AssertImmutableAsync(() => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE public.formal_credential_rotations"));
        var downgrade = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(LegacyGlobalRevocationInheritance.Id));
        Assert.Equal(PostgresErrorCodes.CheckViolation, downgrade.SqlState);
        Assert.Equal("formal_credential_rotation_down_blocked", downgrade.MessageText);
        Assert.Equal(RotationMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(scheduled.HardDeadline, await db.Set<FormalCredentialRotation>().Select(row => row.HardDeadline).SingleAsync());
    }

    [Fact]
    public async Task TheDatabaseRejectsIncompleteOrUnboundedSchedules()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var row = await SeedScheduleAsync(db);
        var recorded = row.RecordedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var deadline = row.HardDeadline.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        Task AppendAsync(Guid id, Guid replacement, string until, string hash) => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.formal_credential_rotations (id, credential_id, replacement_credential_id, environment_id, hard_deadline, recorded_at, intent_hash)
            VALUES ({id}, {row.CredentialId}, {replacement}, {row.EnvironmentId}, CAST({until} AS timestamptz), CAST({recorded} AS timestamptz), {hash})
            """);

        await AssertInvalidAsync(() => AppendAsync(Guid.Empty, row.ReplacementCredentialId, deadline, row.IntentHash));
        await AssertInvalidAsync(() => AppendAsync(row.Id, row.CredentialId, deadline, row.IntentHash));
        await AssertInvalidAsync(() => AppendAsync(row.Id, row.ReplacementCredentialId, recorded, row.IntentHash));
        await AssertInvalidAsync(() => AppendAsync(row.Id, row.ReplacementCredentialId, "infinity", row.IntentHash));
        await AssertInvalidAsync(() => AppendAsync(row.Id, row.ReplacementCredentialId, deadline, row.IntentHash.ToUpperInvariant()));
        var foreign = await Assert.ThrowsAsync<PostgresException>(() => AppendAsync(row.Id, Guid.NewGuid(), deadline, row.IntentHash));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);
        Assert.Equal("FK_formal_credential_rotations_replacement_credential", foreign.ConstraintName);
        Assert.False(await db.Set<FormalCredentialRotation>().AnyAsync());
    }

    private static async Task AssertImmutableAsync(Func<Task> mutate)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(mutate);
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("formal_credential_rotation_immutable", error.MessageText);
    }

    private static Task AssertInvalidAsync(Func<Task> append) =>
        AssertConstraintAsync(append, PostgresErrorCodes.CheckViolation, "ck_formal_credential_rotation");

    private static async Task AssertConstraintAsync(Func<Task> append, string sqlState, string constraint)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(append);
        Assert.Equal(sqlState, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    private static async Task<FormalCredentialRotation> SeedScheduleAsync(WebApiDbContext db)
    {
        var organization = new Organization { Code = "ROT-MIG", Name = "Rotation migration" };
        var project = new Project { OrganizationId = organization.Id, Code = "ROT-MIG", Name = "Rotation migration" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "ROT-MIG", Name = "Rotation migration" };
        var application = new ApplicationRecord { OrganizationId = organization.Id, ProjectId = project.Id,
            Code = "ROT-MIG", Name = "Rotation migration", Owner = "Isolated test" };
        var at = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT date_trunc('second', clock_timestamp()) AS \"Value\"").SingleAsync();
        ApplicationCredential AddCredential(string key) => new() { ApplicationId = application.Id, OrganizationId = organization.Id,
            ProjectId = project.Id, EnvironmentId = environment.Id, BindingMode = "EnvironmentBound", AccessKey = key,
            SecretHash = new string('b', 64), ValidFrom = at.AddMinutes(-1), ExpiresAt = at.AddDays(1) };
        var original = AddCredential("formal-migration-original");
        var replacement = AddCredential("formal-migration-replacement");
        db.AddRange(organization, project, environment, application, original, replacement);
        await db.SaveChangesAsync();
        return new() { Id = Guid.NewGuid(), CredentialId = original.Id, ReplacementCredentialId = replacement.Id, EnvironmentId = environment.Id,
            HardDeadline = at.AddHours(1), RecordedAt = at, IntentHash = new string('a', 64) };
    }

    private static async Task InstallUpgradeAsync(PostgresDatabase database, bool sqlEntry)
    {
        NpgsqlConnection.ClearAllPools();
        if (sqlEntry)
        {
            var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
            var script = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "initial-core.sql"));
            await using var connection = await database.OpenAsync();
            await using var configure = new NpgsqlCommand("SELECT pg_catalog.set_config('webapi.migration_transaction_seconds','900',false),pg_catalog.set_config('webapi.migration_receipt_max_rows','100000',false)", connection);
            await configure.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync();
            return;
        }
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
        GovernanceMigrationRunner.Configure(options);
        await using var runner = new WebApiDbContext(options.Options);
        await MigrationRunnerAssertions.RunAsync(runner, new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864));
    }
}

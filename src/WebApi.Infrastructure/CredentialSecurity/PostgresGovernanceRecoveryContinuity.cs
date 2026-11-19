using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed class PostgresGovernanceRecoveryContinuity : IGovernanceRecoveryContinuity
{
    private readonly GovernanceJournalId journal;
    private readonly IGovernanceJournalWitness journalWitness;
    private readonly IGovernanceEnvironmentHistoryWitness historyWitness;

    public PostgresGovernanceRecoveryContinuity(GovernanceJournalId journal, IGovernanceJournalWitness journalWitness,
        IGovernanceEnvironmentHistoryWitness historyWitness)
    {
        GovernanceJournalProtocol.RequireJournal(journal);
        this.journal = journal;
        this.journalWitness = journalWitness ?? throw new ArgumentNullException(nameof(journalWitness));
        this.historyWitness = historyWitness ?? throw new ArgumentNullException(nameof(historyWitness));
    }

    public async Task<GovernanceContinuity> RequireAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (environmentId == Guid.Empty || db.Database.CurrentTransaction?.GetDbTransaction() is not NpgsqlTransaction tx ||
                tx.IsolationLevel != IsolationLevel.ReadCommitted) throw Unavailable();

            await RequireFenceAsync(db, tx, ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL search_path = pg_catalog, public, pg_temp", ct);
            await RequirePermanentSecurityTablesAsync(db, ct);
            var (position, entries) = await PostgresGovernanceCommitStore.ReadConfirmedHistoryAsync(db, tx, journal, ct);
            foreach (var intent in entries)
                if (!await FormalRotationMutation.VerifyCommittedAsync(db, journal, intent, ct)) throw Unavailable();
            var rotations = entries.Count(intent => FormalRotationJournalIntent.Kind(intent) == FormalRotationJournalIntent.Schedule);
            if (await db.Set<FormalCredentialRotation>().CountAsync(ct) != rotations) throw Unavailable();

            var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(environmentId, ct);
            var origin = await db.Set<EnvironmentSecurityInitialization>().AsNoTracking().SingleAsync(row => row.EnvironmentId == environmentId, ct);
            var initializationHash = GovernanceEnvironmentHistoryProtocol.InitializationHash(new(origin.EnvironmentId, origin.ProjectId,
                origin.OrganizationId, origin.State, origin.EvidenceHash, origin.FreshProofId, origin.ConfirmedBy, origin.ConfirmedAt,
                origin.ConfirmationReason, origin.Revision));
            var checkpoint = new GovernanceEnvironmentHistoryCheckpoint(ledger.OrganizationId, ledger.ProjectId, ledger.SecurityGeneration,
                ledger.Epoch, ledger.HeadHash, initializationHash, ledger.RequireEnvironmentCredentials, ledger.MinimumSnapshotSchema,
                ledger.InitialRequireEnvironmentCredentials);

            var observed = await journalWitness.ObserveAsync(journal, null, ct);
            if (observed is null || observed.Pending is not null || observed.OperationConfirmation is not null ||
                observed.Confirmed != position) throw Unavailable();
            var history = await historyWitness.ObserveAsync(journal, position, environmentId, ct);
            if (history != new GovernanceEnvironmentHistory(journal, position, environmentId, "Confirmed", checkpoint)) throw Unavailable();

            await RequireFenceAsync(db, tx, ct);
            return new(journal, position, ledger);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            ct.ThrowIfCancellationRequested();
            throw Unavailable();
        }
    }

    private static async Task RequireFenceAsync(WebApiDbContext db, NpgsqlTransaction tx, CancellationToken ct)
    {
        if (tx.Connection is not { } connection || !ReferenceEquals(connection, db.Database.GetDbConnection()) ||
            !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), tx)) throw Unavailable();
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_locks held
                WHERE held.pid = pg_catalog.pg_backend_pid() AND held.locktype = 'advisory'
                  AND held.classid = 0 AND held.objid = 8901202 AND held.objsubid = 1
                  AND held.mode = 'ExclusiveLock' AND held.granted
                  AND held.database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database())
            )
            """, connection, tx);
        if (await command.ExecuteScalarAsync(ct) is not true) throw Unavailable();
    }

    private static async Task RequirePermanentSecurityTablesAsync(WebApiDbContext db, CancellationToken ct)
    {
        var present = await db.Database.SqlQueryRaw<bool>("""
            SELECT pg_catalog.count(*) = 12 AS "Value"
              FROM pg_catalog.pg_class relation
              JOIN pg_catalog.pg_namespace ns ON ns.oid = relation.relnamespace
             WHERE ns.nspname = 'public' AND relation.relkind = 'r' AND relation.relpersistence = 'p'
               AND relation.relname IN ('environments', 'environment_security_initializations', 'environment_security_states',
                  'security_events', 'security_operations', 'credential_revocations', 'applications', 'application_credentials',
                  'formal_credential_rotations', 'audit_logs', 'security_outbox_messages', 'security_operation_targets')
            """).SingleAsync(ct);
        if (!present) throw Unavailable();
    }

    private static InvalidDataException Unavailable() => new("credential_governance_recovery_continuity_unavailable");
}

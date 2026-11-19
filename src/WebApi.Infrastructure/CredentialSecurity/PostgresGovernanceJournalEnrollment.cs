using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Migrations;

namespace WebApi.Infrastructure.CredentialSecurity;

/// <summary>Installs the local journal root for one independently provisioned, unused journal.</summary>
public sealed class PostgresGovernanceJournalEnrollment
{
    private readonly string connectionString;
    private readonly GovernanceJournalId journal;
    private readonly IGovernanceJournalWitness witness;

    public PostgresGovernanceJournalEnrollment(string connectionString, GovernanceJournalId journal,
        IGovernanceJournalWitness witness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        GovernanceJournalProtocol.RequireJournal(journal);
        this.connectionString = connectionString;
        this.journal = journal;
        this.witness = witness ?? throw new ArgumentNullException(nameof(witness));
    }

    /// <summary>
    /// Returns true if a root was added, or false for the exact same empty root with freshly verified witness genesis.
    /// The host authorization callback must read permissions using the supplied fenced transaction.
    /// </summary>
    public async Task<bool> EnrollAsync(Func<WebApiDbContext, CancellationToken, Task> authorize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        try
        {
            await using var fence = await PostgresGovernanceWriterFence.AcquireAsync(connectionString, ct);
            await using var transaction = await fence.BeginTransactionAsync(ct);
            var connection = transaction.Connection ?? throw SchemaUnavailable();
            var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection, contextOwnsConnection: false).Options;
            await using var db = new WebApiDbContext(options);
            await using var joined = await db.Database.UseTransactionAsync(transaction, ct) ?? throw SchemaUnavailable();
            // PostgreSQL resolves pg_catalog before the explicit application and temp schemas.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL search_path = public, pg_temp", ct);

            await authorize(db, ct);
            if (!ReferenceEquals(connection, transaction.Connection) ||
                !ReferenceEquals(connection, db.Database.GetDbConnection()) ||
                !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), transaction))
                throw SchemaUnavailable();

            await RequireMigratedSchemaAsync(db, ct);
            var alreadyEnrolled = await ReadEmptyLocalRootAsync(transaction, ct);
            // The trusted witness adapter authenticates a fresh, nonce-bound
            // response scoped to the pinned authority and generation.
            GovernanceWitnessState observed;
            try { observed = await witness.ObserveAsync(journal, null, ct); }
            catch (InvalidDataException) { throw new ApiException(503, "credential_governance_witness_unavailable", "独立安全账本暂不可核验。"); }
            ct.ThrowIfCancellationRequested();
            if (observed != new GovernanceWitnessState(GovernanceJournalProtocol.Genesis, null, null)) throw Conflict();
            if (alreadyEnrolled) return false;

            await db.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit = on", ct);
            if (await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO credential_governance.journal_root (singleton, authority_id, generation, genesis_hash)
                VALUES (true, {journal.AuthorityId}, {journal.Generation}, {GovernanceJournalProtocol.Genesis.Hash})
                """, ct) != 1) throw Conflict();
            await joined.CommitAsync(ct);
            return true;
        }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UndefinedTable or
            PostgresErrorCodes.InvalidSchemaName or PostgresErrorCodes.UndefinedColumn)
        {
            throw SchemaUnavailable();
        }
    }

    private static async Task RequireMigratedSchemaAsync(WebApiDbContext db, CancellationToken ct)
    {
        var applied = await GovernanceMigrationHistory.ReadAppliedAsync(db, ct);
        if (!applied.Contains(CredentialGovernanceJournal.Id, StringComparer.Ordinal)) throw SchemaUnavailable();
        try { await GovernanceMigrationPreflight.ValidateAsync(db, ct); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("governance_migration_preflight_failed:", StringComparison.Ordinal))
        {
            throw SchemaUnavailable();
        }
    }

    private async Task<bool> ReadEmptyLocalRootAsync(NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT pg_catalog.count(*) FROM credential_governance.journal_root),
                EXISTS (SELECT 1 FROM credential_governance.journal_root
                         WHERE singleton AND authority_id = @authority AND generation = @generation AND genesis_hash = @journal_genesis),
                EXISTS (SELECT 1 FROM credential_governance.journal_entries)
                    OR EXISTS (SELECT 1 FROM public.security_operations)
                    OR EXISTS (SELECT 1 FROM public.security_events)
                    OR EXISTS (SELECT 1 FROM public.credential_revocations)
                    OR EXISTS (SELECT 1 FROM public.gateway_security_leases)
                    OR EXISTS (SELECT 1 FROM public.gateway_security_lease_issuances)
                    OR EXISTS (SELECT 1 FROM public.gateway_security_current_receipts)
                    OR EXISTS (SELECT 1 FROM public.environment_security_initializations WHERE state = 'Governed')
                    OR EXISTS (SELECT 1 FROM public.environment_security_states
                                WHERE epoch <> 0 OR head_hash <> @event_genesis OR minimum_snapshot_schema <> '2.0'
                                   OR require_environment_credentials IS DISTINCT FROM initial_require_environment_credentials)
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("authority", journal.AuthorityId);
        command.Parameters.AddWithValue("generation", journal.Generation);
        command.Parameters.AddWithValue("journal_genesis", GovernanceJournalProtocol.Genesis.Hash);
        command.Parameters.AddWithValue("event_genesis", SecurityEventHash.Genesis);
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) throw SchemaUnavailable();
        var count = row.GetInt64(0);
        if (row.GetBoolean(2) || count != 0 && (count != 1 || !row.GetBoolean(1))) throw Conflict();
        return count == 1;
    }

    private static ApiException Conflict() => new(409, "credential_governance_enrollment_conflict", "安全账本的本地或独立状态不符合初始登记条件。");

    private static ApiException SchemaUnavailable() => new(503, "credential_governance_schema_unavailable", "安全账本的数据库结构或写入连接无法核验。");
}

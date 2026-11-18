using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.CredentialSecurity;

// The store owns the transaction. Only ApplyAsync may change business data.
// ApplyAsync must recheck the prepared authorization, scope, revision and
// lease conditions in that same transaction before applying the plan.
// Verification of old operations must use durable effects that survive later writes.
internal interface IPostgresGovernanceMutation
{
    Task AuthorizeAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct);
    Task AuthorizeRecoveryAsync(WebApiDbContext db, CancellationToken ct);
    Task ValidateNewAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct);
    Task ApplyAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct);
    Task<bool> VerifyAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct);
}

internal sealed class PostgresGovernanceCommitStore(string connectionString, IPostgresGovernanceMutation mutation)
    : IGovernanceCommitStore
{
    public async Task<IGovernanceCommitSession> OpenLockedAsync(GovernanceJournalId journal, CancellationToken ct) =>
        await OpenSessionAsync(journal, ct);

    internal async Task<T> InspectAsync<T>(GovernanceJournalId journal, Guid operationId,
        Func<WebApiDbContext, GovernanceLocalJournalState, GovernanceWriteIntent?, CancellationToken, Task<T>> inspect,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        Require(operationId != Guid.Empty);
        await using var session = await OpenSessionAsync(journal, ct);
        return await session.InspectAsync(operationId, inspect, ct);
    }

    private async Task<Session> OpenSessionAsync(GovernanceJournalId journal, CancellationToken ct)
    {
        GovernanceJournalProtocol.RequireJournal(journal);
        var fence = await PostgresGovernanceWriterFence.AcquireAsync(connectionString, ct);
        return new(journal, fence, mutation);
    }

    private sealed class Session(GovernanceJournalId journal, PostgresGovernanceWriterFence fence, IPostgresGovernanceMutation mutation)
        : IGovernanceCommitSession
    {
        public Task<GovernanceLocalJournalState> ReadAsync(CancellationToken ct) =>
            InTransactionAsync(async (db, tx) => (await ReadVerifiedAsync(db, tx, null, ct)).State, ct);

        public Task AuthorizeAsync(GovernanceWriteIntent intent, CancellationToken ct) =>
            InTransactionAsync((db, _) => mutation.AuthorizeAsync(db, intent, ct), ct);

        public Task AuthorizeRecoveryAsync(CancellationToken ct) =>
            InTransactionAsync((db, _) => mutation.AuthorizeRecoveryAsync(db, ct), ct);

        public Task ValidateNewMutationAsync(GovernanceWriteIntent intent, CancellationToken ct) =>
            InTransactionAsync(async (db, tx) =>
            {
                await RequirePreviousAsync(db, tx, intent, ct);
                await mutation.ValidateNewAsync(db, intent, ct);
            }, ct);

        public Task<bool> VerifyCommittedMutationAsync(GovernanceWriteIntent intent, CancellationToken ct) =>
            InTransactionAsync(async (db, tx) => (await ReadVerifiedAsync(db, tx, intent.OperationId, ct)).Requested == intent, ct);

        public Task CommitPreparedAsync(GovernanceWriteIntent intent, CancellationToken ct) =>
            InTransactionAsync(async (db, tx) =>
            {
                var next = GovernanceJournalProtocol.NextPosition(journal, intent);
                await RequirePreviousAsync(db, tx, intent, ct);
                await mutation.ApplyAsync(db, intent, ct);
                RequireCurrentTransaction(db, tx);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                await AppendAsync(tx, intent, next, isConfirmation: false, ct);

                var committed = await ReadVerifiedAsync(db, tx, intent.OperationId, ct);
                Require(committed.State == new GovernanceLocalJournalState(next, intent) && committed.Requested == intent);
            }, ct, commit: true);

        public Task MarkConfirmedAsync(Guid operationId, GovernanceJournalPosition position, CancellationToken ct) =>
            InTransactionAsync(async (db, tx) =>
            {
                var current = await ReadVerifiedAsync(db, tx, operationId, ct);
                var intent = current.State.AwaitingConfirmation ?? throw Inconsistent();
                Require(intent.OperationId == operationId && current.State.Position == position && current.Requested == intent);
                await AppendAsync(tx, intent, position, isConfirmation: true, ct);

                var confirmed = await ReadVerifiedAsync(db, tx, operationId, ct);
                Require(confirmed.State == new GovernanceLocalJournalState(position, null) && confirmed.Requested == intent);
            }, ct, commit: true);

        internal Task<T> InspectAsync<T>(Guid operationId,
            Func<WebApiDbContext, GovernanceLocalJournalState, GovernanceWriteIntent?, CancellationToken, Task<T>> inspect,
            CancellationToken ct) => InTransactionAsync(async (db, tx) =>
            {
                var current = await ReadVerifiedAsync(db, tx, operationId, ct);
                return await inspect(db, current.State, current.Requested, ct);
            }, ct);

        private async Task RequirePreviousAsync(WebApiDbContext db, NpgsqlTransaction tx, GovernanceWriteIntent intent, CancellationToken ct)
        {
            var current = await ReadVerifiedAsync(db, tx, intent.OperationId, ct);
            Require(current.State == new GovernanceLocalJournalState(intent.Previous, null) && current.Requested is null);
        }

        private async Task<JournalRead> ReadVerifiedAsync(WebApiDbContext db, NpgsqlTransaction tx, Guid? operationId, CancellationToken ct)
        {
            var current = await ReadJournalAsync(tx, operationId, ct);
            if (current.Latest is { } latest)
                Require(await mutation.VerifyAsync(db, latest, ct));
            if (current.Requested is { } requested && requested != current.Latest)
                Require(await mutation.VerifyAsync(db, requested, ct));
            return current;
        }

        private async Task<JournalRead> ReadJournalAsync(NpgsqlTransaction tx, Guid? operationId, CancellationToken ct)
        {
            await VerifyRootAsync(tx, ct);
            await using var command = new NpgsqlCommand("""
                SELECT sequence, is_confirmation, operation_id, previous_hash, position_hash, recovery_material
                FROM credential_governance.journal_entries
                ORDER BY sequence, is_confirmation
                """, tx.Connection, tx);
            await using var rows = await command.ExecuteReaderAsync(ct);
            var position = GovernanceJournalProtocol.Genesis;
            GovernanceWriteIntent? latest = null;
            GovernanceWriteIntent? pending = null;
            GovernanceWriteIntent? requested = null;
            var operations = new HashSet<Guid>();
            while (await rows.ReadAsync(ct))
            {
                var recorded = new GovernanceJournalPosition(rows.GetInt64(0), rows.GetString(4));
                var id = rows.GetGuid(2);
                var previousHash = rows.GetString(3);
                if (rows.GetBoolean(1))
                {
                    Require(pending is not null && id == pending.OperationId && previousHash == pending.Previous.Hash &&
                        recorded == position && rows.IsDBNull(5));
                    pending = null;
                    continue;
                }

                Require(pending is null && previousHash == position.Hash && operations.Add(id) && !rows.IsDBNull(5));
                var intent = new GovernanceWriteIntent(id, position, rows.GetString(5));
                Require(GovernanceJournalProtocol.NextPosition(journal, intent) == recorded);
                position = recorded;
                latest = pending = intent;
                if (id == operationId) requested = intent;
            }
            return new(new(position, pending), latest, requested);
        }

        private async Task VerifyRootAsync(NpgsqlTransaction tx, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("""
                SELECT singleton, authority_id, generation, genesis_hash,
                    (SELECT pg_catalog.count(*) = 2
                       FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'credential_governance' AND c.relkind = 'r' AND c.relpersistence = 'p'
                        AND c.relname IN ('journal_root', 'journal_entries'))
                FROM credential_governance.journal_root
                """, tx.Connection, tx);
            await using var rows = await command.ExecuteReaderAsync(ct);
            Require(await rows.ReadAsync(ct) && rows.GetBoolean(0) && rows.GetGuid(1) == journal.AuthorityId &&
                rows.GetGuid(2) == journal.Generation && rows.GetString(3) == GovernanceJournalProtocol.Genesis.Hash &&
                rows.GetBoolean(4) && !await rows.ReadAsync(ct));
        }

        private static async Task AppendAsync(NpgsqlTransaction tx, GovernanceWriteIntent intent,
            GovernanceJournalPosition position, bool isConfirmation, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO credential_governance.journal_entries
                    (sequence, is_confirmation, operation_id, previous_hash, position_hash, recovery_material)
                VALUES (@sequence, @is_confirmation, @id, @previous, @position, @material)
                """, tx.Connection, tx);
            command.Parameters.AddWithValue("sequence", position.Sequence);
            command.Parameters.AddWithValue("is_confirmation", isConfirmation);
            command.Parameters.AddWithValue("id", intent.OperationId);
            command.Parameters.AddWithValue("previous", intent.Previous.Hash);
            command.Parameters.AddWithValue("position", position.Hash);
            command.Parameters.AddWithValue("material", NpgsqlTypes.NpgsqlDbType.Text,
                isConfirmation ? DBNull.Value : intent.RecoveryMaterial);
            Require(await command.ExecuteNonQueryAsync(ct) == 1);
        }

        private Task InTransactionAsync(Func<WebApiDbContext, NpgsqlTransaction, Task> action, CancellationToken ct, bool commit = false) =>
            InTransactionAsync<object?>(async (db, tx) => { await action(db, tx); return null; }, ct, commit);

        private async Task<T> InTransactionAsync<T>(Func<WebApiDbContext, NpgsqlTransaction, Task<T>> action,
            CancellationToken ct, bool commit = false)
        {
            try
            {
                await using var tx = await fence.BeginTransactionAsync(ct);
                var connection = tx.Connection ?? throw Inconsistent();
                var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection, contextOwnsConnection: false).Options;
                await using var db = new WebApiDbContext(options);
                await using var tracked = await db.Database.UseTransactionAsync(tx, ct);
                RequireCurrentTransaction(db, tx);
                await db.Database.ExecuteSqlRawAsync("SET LOCAL search_path = pg_catalog, public, pg_temp", ct);
                if (commit) await db.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit = on", ct);

                var result = await action(db, tx);
                RequireCurrentTransaction(db, tx);
                if (commit)
                {
                    await db.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit = on", ct);
                    await tracked!.CommitAsync(ct);
                }
                return result;
            }
            catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
            {
                await fence.DisposeAfterFailureAsync(error);
                throw Inconsistent(error);
            }
            catch (Exception error)
            {
                // A lost connection or ambiguous commit ends this session. A new
                // coordinator attempt must acquire the fence and observe both sides.
                await fence.DisposeAfterFailureAsync(error);
                throw;
            }
        }

        private static void RequireCurrentTransaction(WebApiDbContext db, NpgsqlTransaction tx) =>
            Require(tx.Connection is { } connection && ReferenceEquals(connection, db.Database.GetDbConnection()) &&
                ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), tx));

        public ValueTask DisposeAsync() => fence.DisposeAsync();

        private sealed record JournalRead(GovernanceLocalJournalState State, GovernanceWriteIntent? Latest, GovernanceWriteIntent? Requested);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw Inconsistent();
    }

    private static InvalidDataException Inconsistent(Exception? inner = null) => new("credential_governance_journal_inconsistent", inner);
}

using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Rejects a detectable database rollback; this is not an independent continuity witness.</summary>
internal static class MigrationEvidenceContinuity
{
    public static async Task VerifyInOwnedTransactionAsync(WebApiDbContext db, IReadOnlyList<Guid> committed, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || db.Database.GetDbConnection() is not NpgsqlConnection connection)
            throw new InvalidOperationException("migration_evidence_history_unverified");
        try
        {
            await using (var table = new NpgsqlCommand("SELECT pg_catalog.to_regclass('public.migration_validation_receipts') IS NOT NULL", connection))
            {
                if (await table.ExecuteScalarAsync(ct) is not true)
                {
                    if (committed.Count != 0) throw new InvalidOperationException("migration_evidence_history_unverified");
                    return;
                }
            }
            // Keep the receipt set stable throughout the transaction. The
            // shared migration advisory lock also covers the initially absent
            // table for both the controlled and standalone SQL paths.
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE public.migration_validation_receipts IN SHARE MODE", ct);
            await using var check = new NpgsqlCommand("SELECT count(*), count(*) FILTER (WHERE attempt_id = ANY(@ids)) FROM public.migration_validation_receipts", connection);
            check.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = committed.ToArray() });
            await using (var reader = await check.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct) || reader.GetInt64(0) != committed.Count || reader.GetInt64(1) != committed.Count)
                    throw new InvalidOperationException("migration_evidence_history_unverified");
            }
            if (committed.Count != 0)
            {
                var identity = await MigrationReceiptIdentityCapture.CaptureAsync(db, ct)
                    ?? throw new InvalidOperationException("migration_evidence_history_unverified");
                await using var identityCheck = new NpgsqlCommand("SELECT count(*) FROM public.migration_validation_receipts WHERE identity_state='Observed' AND cluster_identifier=@cluster AND database_name=@name AND database_oid=@oid", connection);
                identityCheck.Parameters.AddWithValue("cluster", identity.ClusterIdentifier);
                identityCheck.Parameters.AddWithValue("name", identity.DatabaseName);
                identityCheck.Parameters.AddWithValue("oid", identity.DatabaseOid);
                if (await identityCheck.ExecuteScalarAsync(ct) is not long count || count != committed.Count)
                    throw new InvalidOperationException("migration_evidence_history_unverified");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new InvalidOperationException("migration_evidence_history_unverified"); }
    }

    public static MigrationEvidenceReason FailureReason(Exception error) => error switch
    {
        OperationCanceledException => MigrationEvidenceReason.Cancelled,
        InvalidOperationException { Message: "migration_evidence_pending_review" or "migration_evidence_history_unverified" } => MigrationEvidenceReason.PendingReview,
        _ => MigrationEvidenceReason.Storage
    };
}

using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Infrastructure.Persistence;

public enum MigrationReceiptEntry { Managed, Sql }
public enum MigrationReceiptValidation { Unknown, NotApplicable, Passed }
public sealed record MigrationDatabaseIdentity(string ClusterIdentifier, string DatabaseName, long DatabaseOid);

public sealed record MigrationReceipt(
    Guid AttemptId,
    MigrationReceiptEntry EntryPoint,
    string? SourceMigration,
    string TargetMigration,
    MigrationDatabaseIdentity? DatabaseIdentity,
    string? SourceSchemaHash,
    string? MigrationManifestHash,
    MigrationReceiptValidation ValidationState,
    bool? SettingsPresent,
    int? RuleCount,
    long? TotalBytes,
    MigrationBudget Budget,
    string? HistoricalSqlHash,
    string? BuildHash,
    DateTimeOffset RecordedAt,
    int MaxRows)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Budget);
        Budget.Validate();
        if (AttemptId == Guid.Empty || !Enum.IsDefined(EntryPoint) || !Enum.IsDefined(ValidationState) ||
            !MigrationName(TargetMigration) || (SourceMigration is not null && !MigrationName(SourceMigration)) ||
            !Hash(SourceSchemaHash) || !Hash(MigrationManifestHash) || !Hash(HistoricalSqlHash) || !Hash(BuildHash) ||
            RecordedAt == default || RecordedAt.Offset != TimeSpan.Zero || MaxRows is < 1 or > 1000000)
            throw new InvalidOperationException("migration_receipt_invalid");

        if (DatabaseIdentity is { } identity &&
            (string.IsNullOrEmpty(identity.ClusterIdentifier) || identity.ClusterIdentifier.Length > 20 ||
             !identity.ClusterIdentifier.All(character => character is >= '0' and <= '9') ||
             !ulong.TryParse(identity.ClusterIdentifier, NumberStyles.None, CultureInfo.InvariantCulture, out var cluster) || cluster == 0 ||
             string.IsNullOrEmpty(identity.DatabaseName) || identity.DatabaseName.Contains('\0') || Encoding.UTF8.GetByteCount(identity.DatabaseName) > 63 ||
             identity.DatabaseOid is < 1 or > uint.MaxValue))
            throw new InvalidOperationException("migration_receipt_invalid_identity");

        var validResult = ValidationState switch
        {
            MigrationReceiptValidation.Unknown => SettingsPresent is null && RuleCount is null && TotalBytes is null,
            MigrationReceiptValidation.NotApplicable => SettingsPresent == false && RuleCount == 0 && TotalBytes == 0,
            MigrationReceiptValidation.Passed => SettingsPresent.HasValue && RuleCount is >= 0 && RuleCount <= Budget.RuleMaxCount &&
                TotalBytes is >= 0 && TotalBytes <= Budget.TotalMaxBytes && (SettingsPresent == true || RuleCount > 0),
            _ => false
        };
        if (!validResult) throw new InvalidOperationException("migration_receipt_invalid_validation");
    }

    private static bool Hash(string? value) => value is null ||
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool MigrationName(string? value) => value is { Length: >= 16 and <= 150 } &&
        value.Take(14).All(character => character is >= '0' and <= '9') && value[14] == '_' &&
        value.Skip(15).All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}

public static class MigrationReceiptStore
{
    public static async Task InsertAsync(WebApiDbContext db, MigrationReceipt receipt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        if (db.Database.CurrentTransaction is null || db.Database.GetDbConnection().State != ConnectionState.Open)
            throw new InvalidOperationException("migration_receipt_requires_open_tracked_transaction");

        await db.Database.ExecuteSqlRawAsync("LOCK TABLE public.migration_validation_receipts IN SHARE ROW EXCLUSIVE MODE", ct);
        if (await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={receipt.AttemptId}) AS \"Value\"").SingleAsync(ct))
            throw new InvalidOperationException("migration_receipt_duplicate");
        var count = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM (SELECT 1 FROM public.migration_validation_receipts LIMIT {receipt.MaxRows}) AS bounded").SingleAsync(ct);
        if (count >= receipt.MaxRows) throw new InvalidOperationException("migration_receipt_capacity_exceeded");

        var identity = receipt.DatabaseIdentity;
        var identityState = identity is null ? "Unknown" : "Observed";
        var entry = receipt.EntryPoint.ToString();
        var validation = receipt.ValidationState.ToString();
        var timeoutMs = checked((int)receipt.Budget.TransactionTimeout.TotalMilliseconds);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.migration_validation_receipts(
              attempt_id,entry_point,source_migration,target_migration,identity_state,cluster_identifier,database_name,database_oid,
              source_schema_hash,migration_manifest_hash,validation_state,settings_present,rule_count,total_bytes,
              transaction_timeout_ms,settings_max_bytes,rule_max_bytes,rule_max_count,total_max_bytes,receipt_max_rows,
              historical_sql_hash,build_hash,recorded_at)
            VALUES({receipt.AttemptId},{entry},{receipt.SourceMigration},{receipt.TargetMigration},{identityState},{identity?.ClusterIdentifier},
              {identity?.DatabaseName},{identity?.DatabaseOid},{receipt.SourceSchemaHash},{receipt.MigrationManifestHash},{validation},
              {receipt.SettingsPresent},{receipt.RuleCount},{receipt.TotalBytes},{timeoutMs},{receipt.Budget.SettingsMaxBytes},
              {receipt.Budget.RuleMaxBytes},{receipt.Budget.RuleMaxCount},{receipt.Budget.TotalMaxBytes},{receipt.MaxRows},
              {receipt.HistoricalSqlHash},{receipt.BuildHash},{receipt.RecordedAt})
            """, ct);
    }
}

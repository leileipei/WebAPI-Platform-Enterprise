using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace WebApi.Infrastructure.Persistence;

// Npgsql exposes its pinned provider options through this internal API. The
// integration is limited to the locked provider version and checked by tests.
#pragma warning disable EF1001
internal sealed class ManagedNotificationMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions options) : NpgsqlMigrationsSqlGenerator(dependencies, options)
{
    private const string HistoricalSqlHash = "60f4b634d790ee601fd7c42aec1f56fbe28b0f3307b030cd12374bce9bfdffe6";
    private const string HistoricalGuard = "DO $notification_upgrade$";
    private const string HistoricalGuardEnd = "END $notification_upgrade$;";
    private const string HistoricalMigration = "20261008030000_ExternalNotifications";
    private const string HistoricalTable = "notification_channel_profiles";
    private bool managedValidationCompleted;

    internal void EnableManagedValidation() => managedValidationCompleted = true;
    internal void DisableManagedValidation() => managedValidationCompleted = false;

    internal static void VerifyHistoricalMigration(IMigrationsAssembly assembly, string provider)
    {
        if (!assembly.Migrations.TryGetValue(HistoricalMigration, out var migrationType))
            throw new InvalidOperationException("notification_migration_history_unrecognized");
        ValidateHistoricalOperations(assembly.CreateMigration(migrationType, provider).UpOperations);
    }

    internal static void ValidateHistoricalOperations(IReadOnlyList<MigrationOperation> operations)
    {
        if (operations.Count == 0 || operations[0] is not SqlOperation sql
            || sql.SuppressTransaction || sql.GetAnnotations().Any()
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Sql))), HistoricalSqlHash, StringComparison.Ordinal)
            || operations.OfType<CreateTableOperation>().Count(table => table.Name == HistoricalTable && table.Schema is null) != 1
            || operations.OfType<SqlOperation>().Count(item => item.Sql.Contains(HistoricalGuard, StringComparison.Ordinal)) != 1)
            throw new InvalidOperationException("notification_migration_history_unrecognized");
    }

    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        // Identify the historical operation set independently of its SQL
        // marker. A renamed or removed marker must never bypass verification.
        if (operations.OfType<CreateTableOperation>().Any(table => table.Name == HistoricalTable))
            ValidateHistoricalOperations(operations);

        var rewritten = new MigrationOperation[operations.Count];
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (operation is not SqlOperation sql || !sql.Sql.Contains(HistoricalGuard, StringComparison.Ordinal))
            {
                rewritten[i] = operation;
                continue;
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Sql)));
            if (!managedValidationCompleted || sql.SuppressTransaction || sql.GetAnnotations().Any()
                || !string.Equals(hash, HistoricalSqlHash, StringComparison.Ordinal))
                throw new InvalidOperationException("notification_migration_requires_verified_managed_validation");

            var end = sql.Sql.IndexOf(HistoricalGuardEnd, StringComparison.Ordinal);
            if (end < 0 || sql.Sql.IndexOf(HistoricalGuardEnd, end + HistoricalGuardEnd.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("notification_migration_history_unrecognized");
            rewritten[i] = new SqlOperation { Sql = sql.Sql[(end + HistoricalGuardEnd.Length)..], SuppressTransaction = false };
        }
        return base.Generate(rewritten, model, options);
    }
}
#pragma warning restore EF1001

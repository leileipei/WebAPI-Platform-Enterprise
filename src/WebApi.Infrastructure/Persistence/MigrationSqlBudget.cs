using System.Globalization;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Only the limits actually used by the standalone SQL path.</summary>
public sealed record MigrationSqlBudget(TimeSpan TransactionTimeout, int ReceiptMaxRows)
{
    public static MigrationSqlBudget FromEnvironment(Func<string, string?>? getValue = null)
    {
        getValue ??= Environment.GetEnvironmentVariable;
        static int Parse(string? text, int minimum, int maximum)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 7 || !text.All(c => c is >= '0' and <= '9') ||
                !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
                throw new InvalidOperationException("migration_sql_budget_invalid");
            return value;
        }
        return new MigrationSqlBudget(TimeSpan.FromSeconds(Parse(getValue("WEBAPI_MIGRATION_TRANSACTION_SECONDS"), 60, 3600)),
            Parse(getValue("WEBAPI_MIGRATION_RECEIPT_MAX_ROWS"), 1, 1000000));
    }

    public void Validate()
    {
        if (TransactionTimeout.Ticks % TimeSpan.TicksPerSecond != 0 || TransactionTimeout < TimeSpan.FromSeconds(60) ||
            TransactionTimeout > TimeSpan.FromSeconds(3600) || ReceiptMaxRows is < 1 or > 1000000)
            throw new InvalidOperationException("migration_sql_budget_invalid");
    }
}

using System.Globalization;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Explicit limits for one controlled migration attempt.</summary>
public sealed record MigrationBudget(
    TimeSpan TransactionTimeout,
    int SettingsMaxBytes,
    int RuleMaxBytes,
    int RuleMaxCount,
    long TotalMaxBytes)
{
    public static MigrationBudget FromEnvironment(Func<string, string?>? getValue = null)
    {
        getValue ??= Environment.GetEnvironmentVariable;

        long Read(string name, long minimum, long maximum)
        {
            var text = getValue(name);
            if (string.IsNullOrEmpty(text) || text.Length > 10 ||
                !text.All(character => character is >= '0' and <= '9') ||
                !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < minimum || value > maximum)
                throw new InvalidOperationException("migration_budget_invalid: " + name);
            return value;
        }

        var result = new MigrationBudget(
            TimeSpan.FromSeconds(Read("WEBAPI_MIGRATION_TRANSACTION_SECONDS", 60, 3600)),
            checked((int)Read("WEBAPI_MIGRATION_SETTINGS_MAX_BYTES", 1, 1048576)),
            checked((int)Read("WEBAPI_MIGRATION_RULE_MAX_BYTES", 1, 16384)),
            checked((int)Read("WEBAPI_MIGRATION_RULE_MAX_COUNT", 1, 1000000)),
            Read("WEBAPI_MIGRATION_TOTAL_MAX_BYTES", 1, 1073741824));
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (TransactionTimeout.Ticks % TimeSpan.TicksPerSecond != 0 ||
            TransactionTimeout < TimeSpan.FromSeconds(60) || TransactionTimeout > TimeSpan.FromSeconds(3600) ||
            SettingsMaxBytes is < 1 or > 1048576 || RuleMaxBytes is < 1 or > 16384 ||
            RuleMaxCount is < 1 or > 1000000 || TotalMaxBytes is < 1 or > 1073741824)
            throw new InvalidOperationException("migration_budget_invalid");
    }
}

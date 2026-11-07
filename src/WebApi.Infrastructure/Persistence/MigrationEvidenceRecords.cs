using System.Data.Common;
using System.Globalization;
using Npgsql;
using WebApi.Infrastructure.Notifications;

namespace WebApi.Infrastructure.Persistence;

public sealed record MigrationEvidenceOptions(string DirectoryPath, int RecordMaxBytes, long DirectoryQuotaBytes, long MinimumFreeBytes)
{
    public static MigrationEvidenceOptions FromEnvironment(Func<string, string?>? getValue = null)
    {
        getValue ??= Environment.GetEnvironmentVariable;
        long Read(string name)
        {
            var text = getValue(name);
            if (string.IsNullOrEmpty(text) || text.Length > 16 || !text.All(c => c is >= '0' and <= '9') ||
                !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException("migration_evidence_configuration_invalid: " + name);
            return value;
        }
        var maxRecord = Read("WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES");
        if (maxRecord > int.MaxValue) throw new InvalidOperationException("migration_evidence_configuration_invalid");
        var options = new MigrationEvidenceOptions(getValue("WEBAPI_MIGRATION_EVIDENCE_DIR") ?? string.Empty,
            (int)maxRecord, Read("WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES"), Read("WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES"));
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(DirectoryPath) || !Path.IsPathFullyQualified(DirectoryPath) || DirectoryPath == "/" ||
            DirectoryPath.Split('/').Skip(1).Any(segment => segment is "" or "." or "..") ||
            new[] { "/tmp", "/var/tmp", "/run", "/dev", "/proc", "/sys" }.Any(root => DirectoryPath == root || DirectoryPath.StartsWith(root + "/", StringComparison.Ordinal)) ||
            RecordMaxBytes is < 1024 or > 65536 || DirectoryQuotaBytes < 4L * RecordMaxBytes ||
            DirectoryQuotaBytes > 1073741824 || MinimumFreeBytes is < 0 or > 1099511627776)
            throw new InvalidOperationException("migration_evidence_configuration_invalid");
    }
}

public enum MigrationEvidencePhase { Migration, Bootstrap, Seed }
public enum MigrationEvidenceOutcome { Committed, Completed, Failed, Unknown, Skipped }
public enum MigrationEvidenceReason { None, Budget, InvalidNotification, Database, Cancelled, Storage, Unexpected,
    GovernancePreflight, CredentialPreflight, RoutePreflight, OldWriterUnfenced, SameIdentitySession, PendingReview }
public enum MigrationBudgetCause { Unspecified, SettingsTooLarge, RuleTooLarge, RuleCountExceeded, TotalBytesExceeded, TransactionDeadline }
public enum MigrationEvidenceReadState { Missing, Incomplete, Complete, Corrupt }

public sealed record MigrationAttemptStart(
    Guid AttemptId,
    MigrationReceiptEntry EntryPoint,
    DateTimeOffset StartedAt,
    MigrationBudget? Budget,
    bool BootstrapRequested = false,
    bool SeedRequested = false,
    string? BuildHash = null,
    string? SqlHash = null,
    MigrationSqlBudget? SqlBudget = null)
{
    public void Validate()
    {
        if (EntryPoint == MigrationReceiptEntry.Managed && (Budget is null || SqlBudget is not null || SqlHash is not null) ||
            EntryPoint == MigrationReceiptEntry.Sql && (Budget is not null || SqlBudget is null || SqlHash is null || BootstrapRequested || SeedRequested))
            throw new InvalidOperationException("migration_evidence_start_invalid");
        Budget?.Validate();
        SqlBudget?.Validate();
        if (AttemptId == Guid.Empty || !Enum.IsDefined(EntryPoint) || StartedAt == default || StartedAt.Offset != TimeSpan.Zero ||
            !Hash(BuildHash) || !Hash(SqlHash))
            throw new InvalidOperationException("migration_evidence_start_invalid");
    }

    private static bool Hash(string? value) => value is null ||
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record MigrationEvidenceRecord(MigrationEvidencePhase Phase, MigrationEvidenceOutcome Outcome,
    MigrationEvidenceReason Reason, DateTimeOffset ObservedAt, Guid? RuleId = null, MigrationBudgetCause? BudgetCause = null)
{
    public static MigrationEvidenceRecord FromException(MigrationEvidencePhase phase, Exception error, bool rollbackConfirmed, bool transactionDeadlineExpired = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        var (reason, ruleId) = error switch
        {
            NotificationMigrationBudgetException budget => (MigrationEvidenceReason.Budget, budget.RuleId),
            OperationCanceledException => (MigrationEvidenceReason.Cancelled, null),
            PostgresException { SqlState: "P0001" } postgres => ClassifyMessage(postgres.MessageText),
            DbException => (MigrationEvidenceReason.Database, null),
            InvalidOperationException invalid => ClassifyMessage(invalid.Message),
            _ => (MigrationEvidenceReason.Unexpected, null)
        };
        MigrationBudgetCause? cause = error is NotificationMigrationBudgetException notificationBudget ? notificationBudget.Reason switch
        {
            "settings_too_large" => MigrationBudgetCause.SettingsTooLarge,
            "rule_too_large" => MigrationBudgetCause.RuleTooLarge,
            "rule_count_exceeded" => MigrationBudgetCause.RuleCountExceeded,
            "total_bytes_exceeded" => MigrationBudgetCause.TotalBytesExceeded,
            _ => MigrationBudgetCause.Unspecified
        } : null;
        if (phase == MigrationEvidencePhase.Migration && transactionDeadlineExpired && error is OperationCanceledException)
        {
            reason = MigrationEvidenceReason.Budget;
            cause = MigrationBudgetCause.TransactionDeadline;
        }
        return new MigrationEvidenceRecord(phase, rollbackConfirmed && reason != MigrationEvidenceReason.PendingReview ? MigrationEvidenceOutcome.Failed : MigrationEvidenceOutcome.Unknown,
            reason, DateTimeOffset.UtcNow, ruleId, cause);
    }

    private static (MigrationEvidenceReason, Guid?) ClassifyMessage(string message)
    {
        if (message == "migration_evidence_history_unverified") return (MigrationEvidenceReason.PendingReview, null);
        if (message == "governance_migration_preflight_failed: old_writer_unfenced") return (MigrationEvidenceReason.OldWriterUnfenced, null);
        if (message == "governance_migration_preflight_failed: same_identity_session") return (MigrationEvidenceReason.SameIdentitySession, null);
        if (message.StartsWith("governance_migration_preflight_failed:", StringComparison.Ordinal)) return (MigrationEvidenceReason.GovernancePreflight, null);
        if (message.StartsWith("credential_governance_preflight_failed:", StringComparison.Ordinal)) return (MigrationEvidenceReason.CredentialPreflight, null);
        if (message.StartsWith("route_governance_preflight_failed:", StringComparison.Ordinal)) return (MigrationEvidenceReason.RoutePreflight, null);
        if (message == "Invalid legacy notification settings: system.notification.") return (MigrationEvidenceReason.InvalidNotification, null);
        const string prefix = "Invalid legacy rule notification: ";
        if (message.StartsWith(prefix, StringComparison.Ordinal) && message.Length == prefix.Length + 37 && message[^1] == '.' &&
            Guid.TryParseExact(message.AsSpan(prefix.Length, 36), "D", out var ruleId) && ruleId != Guid.Empty)
            return (MigrationEvidenceReason.InvalidNotification, ruleId);
        return (MigrationEvidenceReason.Unexpected, null);
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Phase) || !Enum.IsDefined(Outcome) || !Enum.IsDefined(Reason) || ObservedAt == default || ObservedAt.Offset != TimeSpan.Zero ||
            (BudgetCause.HasValue && (Reason != MigrationEvidenceReason.Budget || !Enum.IsDefined(BudgetCause.Value))) ||
            (RuleId.HasValue && (RuleId == Guid.Empty || Reason is not (MigrationEvidenceReason.Budget or MigrationEvidenceReason.InvalidNotification))) ||
            (Phase == MigrationEvidencePhase.Migration && Outcome is not (MigrationEvidenceOutcome.Committed or MigrationEvidenceOutcome.Failed or MigrationEvidenceOutcome.Unknown)) ||
            (Phase != MigrationEvidencePhase.Migration && Outcome is not (MigrationEvidenceOutcome.Completed or MigrationEvidenceOutcome.Failed or MigrationEvidenceOutcome.Unknown or MigrationEvidenceOutcome.Skipped)) ||
            ((Outcome is MigrationEvidenceOutcome.Committed or MigrationEvidenceOutcome.Completed or MigrationEvidenceOutcome.Skipped) && Reason != MigrationEvidenceReason.None) ||
            (Outcome == MigrationEvidenceOutcome.Failed && Reason == MigrationEvidenceReason.None))
            throw new InvalidOperationException("migration_evidence_record_invalid");
    }
}

public sealed record MigrationEvidenceRead(MigrationEvidenceReadState State, MigrationAttemptStart? Start, IReadOnlyList<MigrationEvidenceRecord> Records);
public sealed record MigrationEvidenceArchiveInspection(int Attempts, int PendingAttempts, string ContentHash);
public sealed record MigrationEvidenceDirectoryInspection(int Attempts, int PendingAttempts, string ContentHash);

public interface IMigrationEvidenceJournal
{
    Task<IReadOnlyList<Guid>> ReadCommittedAttemptsAsync(CancellationToken ct);
    Task StartAsync(MigrationAttemptStart start, CancellationToken ct);
    Task StartCheckedAsync(MigrationAttemptStart start, IReadOnlyList<Guid> checkedCommittedAttempts, CancellationToken ct);
    Task AppendAsync(Guid attemptId, MigrationEvidenceRecord record, CancellationToken ct);
    Task<MigrationEvidenceRead> ReadAsync(Guid attemptId, CancellationToken ct);
}

namespace WebApi.Infrastructure.Notifications;

public enum NotificationValidationStatus
{
    NotApplicable,
    Passed
}

public sealed record NotificationValidationSummary(
    NotificationValidationStatus Status,
    bool SettingsPresent,
    int RuleCount,
    long TotalBytes);

public sealed class NotificationMigrationBudgetException : InvalidOperationException
{
    internal NotificationMigrationBudgetException(string reason, Guid? ruleId = null)
        : base("notification_migration_budget_exceeded: " + reason + (ruleId.HasValue ? ": " + ruleId.Value.ToString("D") : string.Empty))
    {
        Reason = reason;
        RuleId = ruleId;
    }

    public string Reason { get; }
    public Guid? RuleId { get; }
}

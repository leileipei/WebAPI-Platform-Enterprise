namespace WebApi.Domain.Applications;

// Inputs are taken from the witnessed schedule, complete lease issuance
// history and trusted time bounds under the writer/issuer fence.
public static class RotationLeaseDeadlinePolicy
{
    public static bool CanPrepare(DateTimeOffset hardDeadline, DateTimeOffset conservativeNowUpper,
        TimeSpan maximumClockSkew, TimeSpan deliveryAndApplyBudget, IReadOnlyCollection<DateTimeOffset>? possibleLeaseExpiries)
    {
        var boundary = Cutoff(hardDeadline, maximumClockSkew);
        return boundary is { } limit && HasTimeToApply(limit, conservativeNowUpper, deliveryAndApplyBudget) &&
            possibleLeaseExpiries is not null && possibleLeaseExpiries.All(expiry => ValidUtc(expiry) && expiry <= limit);
    }

    // Null means no Allow lease can be issued. Expiry must satisfy every
    // active rotation's boundary; pass the earliest outstanding deadline.
    public static DateTimeOffset? CapExpiry(DateTimeOffset hardDeadline, DateTimeOffset conservativeNowUpper,
        DateTimeOffset proposedExpiry, DateTimeOffset trustedTimeValidUntil, TimeSpan maximumClockSkew,
        TimeSpan deliveryAndApplyBudget)
    {
        var boundary = Cutoff(hardDeadline, maximumClockSkew);
        if (boundary is not DateTimeOffset limit || !ValidUtc(proposedExpiry) || !ValidUtc(trustedTimeValidUntil)) return null;
        var expiry = new DateTimeOffset(Math.Min(limit.Ticks, Math.Min(proposedExpiry.Ticks, trustedTimeValidUntil.Ticks)), TimeSpan.Zero);
        return HasTimeToApply(expiry, conservativeNowUpper, deliveryAndApplyBudget) ? expiry : null;
    }

    private static DateTimeOffset? Cutoff(DateTimeOffset deadline, TimeSpan skew) =>
        ValidUtc(deadline) && skew >= TimeSpan.Zero && skew <= TimeSpan.FromSeconds(2) && deadline.Ticks > skew.Ticks
            ? deadline - skew : null;

    private static bool HasTimeToApply(DateTimeOffset expiry, DateTimeOffset nowUpper, TimeSpan budget) =>
        ValidUtc(nowUpper) && budget >= TimeSpan.Zero && expiry - nowUpper > budget;

    private static bool ValidUtc(DateTimeOffset time) => time.Offset == TimeSpan.Zero && time > DateTimeOffset.MinValue;
}

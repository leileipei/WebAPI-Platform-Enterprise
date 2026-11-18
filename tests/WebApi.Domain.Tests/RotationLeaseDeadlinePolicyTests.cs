using WebApi.Domain.Applications;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class RotationLeaseDeadlinePolicyTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 11, 18, 1, 2, 3, TimeSpan.Zero);
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ApplyBudget = TimeSpan.FromSeconds(1);

    [Fact]
    public void PreparationRequiresCompleteHistoryAndIncludesTheMostRestrictiveLeaseBoundary()
    {
        var boundary = Deadline - Skew;
        var nowUpper = boundary.AddSeconds(-5);
        Assert.True(RotationLeaseDeadlinePolicy.CanPrepare(Deadline, nowUpper, Skew, ApplyBudget, []));
        Assert.True(RotationLeaseDeadlinePolicy.CanPrepare(Deadline, nowUpper, Skew, ApplyBudget, [boundary.AddSeconds(-3), boundary]));
        Assert.False(RotationLeaseDeadlinePolicy.CanPrepare(Deadline, nowUpper, Skew, ApplyBudget, null));
        Assert.False(RotationLeaseDeadlinePolicy.CanPrepare(Deadline, nowUpper, Skew, ApplyBudget, [boundary, boundary.AddTicks(1)]));
        Assert.False(RotationLeaseDeadlinePolicy.CanPrepare(Deadline, nowUpper, Skew, ApplyBudget, [DateTimeOffset.MinValue]));
    }

    [Fact]
    public void RenewalCapsBothRequestedWindowAndIndependentTimeValidity()
    {
        var boundary = Deadline - Skew;
        var nowUpper = boundary.AddSeconds(-5);
        Assert.Equal(boundary, RotationLeaseDeadlinePolicy.CapExpiry(Deadline, nowUpper, Deadline, Deadline, Skew, ApplyBudget));
        Assert.Equal(boundary.AddSeconds(-2), RotationLeaseDeadlinePolicy.CapExpiry(Deadline, nowUpper, Deadline, boundary.AddSeconds(-2), Skew, ApplyBudget));
        Assert.Equal(boundary.AddSeconds(-3), RotationLeaseDeadlinePolicy.CapExpiry(Deadline, nowUpper, boundary.AddSeconds(-3), Deadline, Skew, ApplyBudget));
        Assert.Null(RotationLeaseDeadlinePolicy.CapExpiry(Deadline, boundary - ApplyBudget, Deadline, Deadline, Skew, ApplyBudget));
        Assert.Null(RotationLeaseDeadlinePolicy.CapExpiry(Deadline, boundary.AddTicks(1), Deadline, Deadline, Skew, ApplyBudget));
    }

    [Fact]
    public void UnreliableTimesAndBudgetsCannotCreateAdmissionTime()
    {
        var nowUpper = Deadline.AddSeconds(-10);
        Assert.Null(RotationLeaseDeadlinePolicy.CapExpiry(Deadline, nowUpper.ToOffset(TimeSpan.FromHours(1)), Deadline, Deadline, Skew, ApplyBudget));
        Assert.Null(RotationLeaseDeadlinePolicy.CapExpiry(Deadline, nowUpper, Deadline, Deadline, Skew.Add(TimeSpan.FromTicks(1)), ApplyBudget));
        Assert.Null(RotationLeaseDeadlinePolicy.CapExpiry(Deadline, nowUpper, Deadline, Deadline, Skew, -ApplyBudget));
        Assert.Null(RotationLeaseDeadlinePolicy.CapExpiry(DateTimeOffset.MinValue.AddSeconds(1), nowUpper, Deadline, Deadline, Skew, ApplyBudget));
    }
}

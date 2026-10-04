using WebApi.Contracts.Observability;
using WebApi.Domain.Observability;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class ObservationQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SevenDaysAndHundredRowsAreAllowed() =>
        ObservationQueryValidator.Validate(new(Now.AddDays(-7), Now), 100, Now);

    [Theory]
    [InlineData(8, 50)]
    [InlineData(1, 101)]
    [InlineData(1, 0)]
    public void EightDaysOr101RowsAreRejected(int days, int limit) =>
        Assert.Throws<ArgumentException>(() => ObservationQueryValidator.Validate(new(Now.AddDays(-days), Now), limit, Now));

    [Fact]
    public void ReversedRangeAndFutureEndRejected()
    {
        Assert.Throws<ArgumentException>(() => ObservationQueryValidator.Validate(new(Now, Now.AddMinutes(-1)), 50, Now));
        Assert.Throws<ArgumentException>(() => ObservationQueryValidator.Validate(new(Now, Now), 50, Now));
        Assert.Throws<ArgumentException>(() => ObservationQueryValidator.Validate(new(Now.AddHours(-1), Now.AddSeconds(31)), 50, Now));
    }

    [Fact]
    public void ClockSkewAtThirtySecondsIsAllowed() =>
        ObservationQueryValidator.Validate(new(Now.AddHours(-1), Now.AddSeconds(30)), 50, Now);

    [Fact]
    public void EquivalentOffsetRangeUsesElapsedUtcTime()
    {
        var range = new TimeRange(Now.AddDays(-7).ToOffset(TimeSpan.FromHours(8)), Now.ToOffset(TimeSpan.FromHours(-5)));
        ObservationQueryValidator.Validate(range, 50, Now);
        Assert.Equal(TimeSpan.Zero, range.Start.Offset);
        Assert.Equal(TimeSpan.Zero, range.End.Offset);
    }
}

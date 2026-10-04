using WebApi.Contracts.Observability;

namespace WebApi.Domain.Observability;

public static class ObservationQueryValidator
{
    public static void Validate(TimeRange range, int limit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (range.Start >= range.End || range.End - range.Start > TimeSpan.FromDays(7))
            throw new ArgumentException("查询时间范围必须大于零且不超过七天。", nameof(range));
        if (range.End > now.ToUniversalTime().AddSeconds(30))
            throw new ArgumentException("查询截止时间不能超出服务端当前时间。", nameof(range));
        if (limit is < 1 or > 100)
            throw new ArgumentException("每页记录数必须在1到100之间。", nameof(limit));
    }
}

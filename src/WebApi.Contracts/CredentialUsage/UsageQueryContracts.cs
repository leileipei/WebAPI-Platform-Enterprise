using WebApi.Contracts.Common;
namespace WebApi.Contracts.CredentialUsage;
public sealed record UsageWindow(DateTimeOffset From,DateTimeOffset To);
public sealed record UsageCoverageDto(string State,DateTimeOffset? CoverageStart,DateTimeOffset? CompleteThrough,DateTimeOffset? Freshness,IReadOnlyList<Guid> MissingNodes,IReadOnlyList<UsageGap> Gaps,string? Reason);
public sealed record CredentialUsageRow(Guid CredentialId,string AccessKeyLast4,string? Count,DateTimeOffset? LastObservedAt,DateTimeOffset? VisibleLastUsedAt,UsageCoverageDto Coverage);
public sealed record CredentialUsagePage(UsageWindow Window,IReadOnlyList<Guid> VisibleEnvironmentIds,PageResult<CredentialUsageRow> Page,UsageCoverageDto Coverage);

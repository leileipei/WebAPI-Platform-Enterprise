namespace WebApi.Contracts.Releases;

public sealed record ApprovalEligibility(bool CanAct, int? CurrentStepOrder, string? ReasonCode);

public sealed record ApprovalInboxFilter(string View, Guid? OrganizationId, Guid? ProjectId, Guid? EnvironmentId, string? Status, int Page = 1, int PageSize = 50);
public sealed record ApprovalInboxCounts(int PendingMine, int HandledMine, int AllVisible);
public sealed record ApprovalScopeDto(Guid Id, string Code, string Name);
public sealed record ApprovalRiskDto(string Visibility, string? Coverage, WebApi.Contracts.Comparisons.ComparisonCounts? Counts, int? ReviewCount);
public sealed record ApprovalInboxItemDto(Guid Id, string ReleaseNo, string ReleaseType, string State,
    ApprovalScopeDto Organization, ApprovalScopeDto Project, ApprovalScopeDto Environment,
    Guid RequestedBy, string ApplicantDisplayName, DateTimeOffset CreatedAt,
    ApprovalEligibility ApprovalEligibility, int ApprovedCount, int RequiredCount, ApprovalRiskDto Risk,string? ApplicationKind=null,ApprovalDeliverySummaryDto? Delivery=null);
public sealed record ApprovalInboxPageDto(WebApi.Contracts.Common.PageResult<ApprovalInboxItemDto> Page, ApprovalInboxCounts Counts, ApprovalInboxFilter? Filter = null);

public sealed record ApprovalDeliverySummaryDto(string Visibility,Guid? PromotionId,Guid? ArtifactId,string? ArtifactHash,ApprovalScopeDto? SourceEnvironment);

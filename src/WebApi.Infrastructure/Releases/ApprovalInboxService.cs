using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Releases;

public sealed class ApprovalInboxService(WebApiDbContext db, ApprovalEligibilityService eligibility)
{
    private static readonly string[] states = ["Draft", "WaitingApproval", "Ready", "Building", "Publishing", "Succeeded", "Failed", "Cancelled", "Rejected", "RolledBack"];

    public async Task<ApprovalInboxPageDto> ListAsync(ApprovalInboxFilter filter, ActorContext actor, CancellationToken ct)
    {
        if (filter.View is not ("PendingMine" or "HandledMine" or "AllVisible") || filter.Page < 1 || filter.PageSize is < 1 or > 100
            || filter.Status is not null && !states.Contains(filter.Status))
            throw new ApiException(422, "invalid_approval_filter", "审批筛选或分页参数无效。");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, budget.Token);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '10000'", budget.Token);
            var result = await QueryAsync(filter, actor, budget.Token);
            await transaction.CommitAsync(budget.Token);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested) { throw Timeout(); }
        catch (PostgresException e) when (e.SqlState == "57014" && !ct.IsCancellationRequested) { throw Timeout(); }
    }

    private static ApiException Timeout() => new(503, "approval_query_timeout", "审批查询超时，请稍后重试。");

    private async Task<ApprovalInboxPageDto> QueryAsync(ApprovalInboxFilter filter, ActorContext actor, CancellationToken ct)
    {
        if (!await db.Set<UserRecord>().AnyAsync(u => u.Id == actor.UserId && u.Status == "Active", ct))
            throw new ApiException(401, "inactive_session", "会话已失效。");
        var visible = await ReadableEnvironments(actor).TagWith("approval-inbox-authorized-environments").ToArrayAsync(ct);
        // Resolve filters only against the current readable hierarchy, including its real ancestors.
        if (filter.EnvironmentId is Guid environment && !visible.Any(e => e.Id == environment
            && (filter.ProjectId is null || e.ProjectId == filter.ProjectId) && (filter.OrganizationId is null || e.OrganizationId == filter.OrganizationId))) throw ScopeResolver.Missing();
        if (filter.ProjectId is Guid project && !visible.Any(e => e.ProjectId == project
            && (filter.OrganizationId is null || e.OrganizationId == filter.OrganizationId))) throw ScopeResolver.Missing();
        if (filter.OrganizationId is Guid organization && !visible.Any(e => e.OrganizationId == organization)) throw ScopeResolver.Missing();
        visible = visible.Where(e => (filter.EnvironmentId is null || e.Id == filter.EnvironmentId)
            && (filter.ProjectId is null || e.ProjectId == filter.ProjectId) && (filter.OrganizationId is null || e.OrganizationId == filter.OrganizationId)).ToArray();
        var environmentIds = visible.Select(e => e.Id).ToArray();
        var actionable = eligibility.QueryActionableReleaseIds(actor, environmentIds);
        var releases = db.Set<ReleaseRecord>().AsNoTracking().Where(r => environmentIds.Contains(r.EnvironmentId)
            && db.Set<ApprovalTask>().Any(t => t.ReleaseId == r.Id));
        var pending = releases.Where(r => actionable.Contains(r.Id));
        var handled = releases.Where(r => db.Set<ApprovalTask>().Any(t => t.ReleaseId == r.Id && t.AssigneeUserId == actor.UserId && (t.Status == "Approved" || t.Status == "Rejected")));
        var counts = new ApprovalInboxCounts(await pending.TagWith("approval-inbox-pending-count").CountAsync(ct),
            await handled.TagWith("approval-inbox-handled-count").CountAsync(ct), await releases.TagWith("approval-inbox-visible-count").CountAsync(ct));
        var selected = filter.View switch { "PendingMine" => pending, "HandledMine" => handled, _ => releases };
        if (filter.Status is not null) selected = selected.Where(r => r.Status == filter.Status);
        var total = await selected.TagWith("approval-inbox-page-count").CountAsync(ct);
        var offset = ((long)filter.Page - 1) * filter.PageSize;
        if (offset >= total) return new(new([], total, filter.Page, filter.PageSize), counts, filter);
        var page = await (from r in selected.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip((int)offset).Take(filter.PageSize)
            join applicant in db.Set<UserRecord>() on r.RequestedBy equals applicant.Id
            let currentStep = db.Set<ApprovalTask>().Where(t => t.ReleaseId == r.Id && t.Status == "Pending").Min(t => (int?)t.StepOrder)
            select new InboxRow
            {
                Id = r.Id, EnvironmentId = r.EnvironmentId, ReleaseNo = r.ReleaseNo, State = r.Status,
                ReleaseType = r.ReleaseType, RequestedBy = r.RequestedBy, ApplicantDisplayName = applicant.DisplayName,
                CreatedAt = r.CreatedAt, Policy = r.ApprovalPolicy, CurrentStep = currentStep,
                ApprovedCount = db.Set<ApprovalTask>().Count(t => t.ReleaseId == r.Id && t.Status == "Approved" && (currentStep == null || t.StepOrder == currentStep)),
                CanAct = actionable.Contains(r.Id)
            }).TagWith("approval-inbox-bounded-page").ToArrayAsync(ct);
        var risks = await RisksAsync(page, visible, actor, ct);
        var scopeByEnvironment = visible.ToDictionary(e => e.Id);
        var items = page.Select(row =>
        {
            var scope = scopeByEnvironment[row.EnvironmentId];
            var rules = row.Policy is null ? [] : JsonSerializer.Deserialize<ApprovalRule[]>(row.Policy, CanonicalJson.Options) ?? [];
            var required = row.CurrentStep is int step ? rules.Single(r => r.StepOrder == step).RequiredCount : rules.Sum(r => r.RequiredCount);
            return new ApprovalInboxItemDto(row.Id, row.ReleaseNo, row.ReleaseType, row.State,
                new(scope.OrganizationId, scope.OrganizationCode, scope.OrganizationName), new(scope.ProjectId, scope.ProjectCode, scope.ProjectName),
                new(scope.Id, scope.Code, scope.Name), row.RequestedBy, row.ApplicantDisplayName, row.CreatedAt,
                new(row.CanAct, row.CurrentStep, row.CanAct ? null : "approval_not_available"), row.ApprovedCount, required, risks[row.Id]);
        }).ToArray();
        return new(new(items, total, filter.Page, filter.PageSize), counts, filter);
    }

    private IQueryable<ReadableEnvironment> ReadableEnvironments(ActorContext actor) =>
        from env in db.Set<EnvironmentRecord>() join project in db.Set<Project>() on env.ProjectId equals project.Id
        join organization in db.Set<Organization>() on project.OrganizationId equals organization.Id
        where env.Status == "Active" && project.Status == "Active" && organization.Status == "Active"
            && db.Set<UserProjectScope>().Any(g => g.UserId == actor.UserId && g.OrganizationId == organization.Id
                && (g.ProjectId == null || g.ProjectId == project.Id) && (g.EnvironmentId == null || g.EnvironmentId == env.Id))
            && (from ur in db.Set<UserRole>() join role in db.Set<Role>() on ur.RoleId equals role.Id
                join rp in db.Set<RolePermission>() on role.Id equals rp.RoleId join permission in db.Set<Permission>() on rp.PermissionId equals permission.Id
                where ur.UserId == actor.UserId && permission.Code == "release.read" && (role.OrganizationId == null || role.OrganizationId == organization.Id)
                select permission.Id).Any()
        select new ReadableEnvironment
        {
            Id = env.Id, Code = env.Code, Name = env.Name, ProjectId = project.Id, ProjectCode = project.Code, ProjectName = project.Name,
            OrganizationId = organization.Id, OrganizationCode = organization.Code, OrganizationName = organization.Name
        };

    private async Task<Dictionary<Guid, ApprovalRiskDto>> RisksAsync(InboxRow[] page, ReadableEnvironment[] visible, ActorContext actor, CancellationToken ct)
    {
        var ids = page.Select(p => p.Id).ToArray();
        // Only this authorized page is decoded in PostgreSQL. Credentials, full candidates
        // and free-text review comments never leave the database in this query.
        var projections = await db.Database.SqlQuery<RiskProjection>($$"""
            SELECT safe.id AS "Id", CASE WHEN safe.candidate IS NULL THEN NULL ELSE jsonb_build_object(
              'apiIds', (SELECT jsonb_agg(version#>>'{api,id}') FROM jsonb_array_elements(safe.candidate->'versions') version),
              'reviews', (SELECT jsonb_agg(jsonb_build_object('apiId', review->'apiId',
                'coverage', review#>'{summary,coverage}', 'counts', review#>'{summary,counts}'))
                FROM jsonb_array_elements(COALESCE(NULLIF(safe.candidate->'riskReviewReferences', 'null'::jsonb), '[]'::jsonb)) review)) END::text AS "Json"
            FROM (SELECT r.id, CASE WHEN r.candidate_bytes IS NULL THEN NULL ELSE convert_from(r.candidate_bytes, 'UTF8')::jsonb END candidate
              FROM release_records r WHERE r.id = ANY({{ids}})) safe
            """).TagWith("approval-inbox-bounded-risk").ToArrayAsync(ct);
        var parsed = projections.ToDictionary(p => p.Id, p => p.Json is null ? null : JsonDocument.Parse(p.Json));
        try
        {
            var apiIds = parsed.Values.Where(p => p is not null).SelectMany(p => ApiIds(p!.RootElement)).Distinct().ToArray();
            string[] codes = ["api.read", "api.version.read", "api.schema.read"];
            var permissions = await (from api in db.Set<Api>()
                join project in db.Set<Project>() on api.ProjectId equals project.Id
                join organization in db.Set<Organization>() on api.OrganizationId equals organization.Id
                where apiIds.Contains(api.Id) && project.OrganizationId == api.OrganizationId && project.Status == "Active" && organization.Status == "Active"
                    && db.Set<UserProjectScope>().Any(g => g.UserId == actor.UserId && g.OrganizationId == api.OrganizationId
                        && (g.ProjectId == null || g.ProjectId == api.ProjectId) && g.EnvironmentId == null)
                from ur in db.Set<UserRole>().Where(u => u.UserId == actor.UserId)
                join role in db.Set<Role>() on ur.RoleId equals role.Id
                join rp in db.Set<RolePermission>() on role.Id equals rp.RoleId
                join permission in db.Set<Permission>() on rp.PermissionId equals permission.Id
                where codes.Contains(permission.Code) && (role.OrganizationId == null || role.OrganizationId == api.OrganizationId)
                select new { api.Id, api.OrganizationId, api.ProjectId, permission.Code }).Distinct().TagWith("approval-inbox-risk-permissions").ToArrayAsync(ct);
            var readableApis = permissions.GroupBy(p => p.Id).Where(g => g.Select(p => p.Code).Distinct().Count() == codes.Length).ToDictionary(g => g.Key, g => g.First());
            var scopeByEnvironment = visible.ToDictionary(e => e.Id);
            var result = new Dictionary<Guid, ApprovalRiskDto>();
            foreach (var row in page)
            {
                var document = parsed[row.Id];
                if (document is null) { result[row.Id] = new("Unavailable", null, null, null); continue; }
                var scope = scopeByEnvironment[row.EnvironmentId]; var root = document.RootElement;
                if (ApiIds(root).Any(id => !readableApis.TryGetValue(id, out var api) || api.OrganizationId != scope.OrganizationId || api.ProjectId != scope.ProjectId))
                { result[row.Id] = new("Restricted", null, null, null); continue; }
                var reviews = root.GetProperty("reviews");
                if (reviews.ValueKind == JsonValueKind.Null || reviews.GetArrayLength() == 0) { result[row.Id] = new("NoReviews", null, null, 0); continue; }
                var counts = reviews.EnumerateArray().Select(r => r.GetProperty("counts").Deserialize<ComparisonCounts>(CanonicalJson.Options)!).ToArray();
                var coverage = reviews.EnumerateArray().Select(r => r.GetProperty("coverage").GetString()).ToArray();
                result[row.Id] = new("Visible", coverage.Contains("Invalid") ? "Invalid" : coverage.Any(c => c != "Complete") ? "Partial" : "Complete",
                    new(counts.Sum(c => c.Added), counts.Sum(c => c.Changed), counts.Sum(c => c.Removed), counts.Sum(c => c.Compatible), counts.Sum(c => c.Breaking), counts.Sum(c => c.Unknown)), counts.Length);
            }
            return result;
        }
        finally { foreach (var document in parsed.Values) document?.Dispose(); }
    }

    private static IEnumerable<Guid> ApiIds(JsonElement root)
    {
        if (root.GetProperty("apiIds").ValueKind == JsonValueKind.Array)
            foreach (var id in root.GetProperty("apiIds").EnumerateArray()) yield return id.GetGuid();
        if (root.GetProperty("reviews").ValueKind == JsonValueKind.Array)
            foreach (var review in root.GetProperty("reviews").EnumerateArray()) yield return review.GetProperty("apiId").GetGuid();
    }

    private sealed class ReadableEnvironment
    {
        public Guid Id { get; set; } public string Code { get; set; } = ""; public string Name { get; set; } = "";
        public Guid ProjectId { get; set; } public string ProjectCode { get; set; } = ""; public string ProjectName { get; set; } = "";
        public Guid OrganizationId { get; set; } public string OrganizationCode { get; set; } = ""; public string OrganizationName { get; set; } = "";
    }
    private sealed class InboxRow
    {
        public Guid Id { get; set; } public Guid EnvironmentId { get; set; } public string ReleaseNo { get; set; } = "";
        public string ReleaseType { get; set; } = ""; public string State { get; set; } = ""; public Guid RequestedBy { get; set; }
        public string ApplicantDisplayName { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } public string? Policy { get; set; }
        public int? CurrentStep { get; set; } public bool CanAct { get; set; } public int ApprovedCount { get; set; }
    }
    private sealed class RiskProjection { public Guid Id { get; set; } public string? Json { get; set; } }
}

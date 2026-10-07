using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace WebApi.Integration.Tests;

public sealed class ApprovalInboxTests(ITestOutputHelper output)
{
    private static ApprovalInboxFilter Filter(string view = "PendingMine", int page = 1, int size = 50) => new(view, null, null, null, null, page, size);
    private static ApprovalInboxService Service(WebApiDbContext db) => new(db, new ApprovalEligibilityService(db, new AuthorizationService(db)));
    private static async Task<Guid> SubmittedAsync(ApiFixture api)
    { await api.InitializeAsync(); await api.SeedReleaseAsync(); using var login = await api.LoginAsync(); login.EnsureSuccessStatusCode(); return await api.CreateSubmittedReleaseAsync(); }

    // Filtering after Take(50) would lose the only actionable release, and seats must not duplicate rows.
    [Fact] public async Task MineOnSecondPageCountsBeforePagination()
    {
        await using var api = new ApiFixture(); var mine = await SubmittedAsync(api);
        var reviewer = await api.NewReviewerAsync("ApiApprover");
        await using var db = api.Context(); var original = await db.Set<ReleaseRecord>().SingleAsync(r => r.Id == mine);
        original.CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.Add(new ApprovalTask { ReleaseId = mine, FlowId = api.ApprovalFlow.Id, StepOrder = 1, Status = "Pending" });
        original.ApprovalPolicy = JsonSerializer.Serialize(new[] { new ApprovalRule(1, "ApiApprover", 2), new ApprovalRule(2, "SecurityReviewer", 1) }, CanonicalJson.Options);
        for (var i = 0; i < 50; i++)
        {
            var release = Copy(original, api.Environment.Id); release.CreatedAt = original.CreatedAt.AddMinutes(i + 1);
            release.ApprovalPolicy = JsonSerializer.Serialize(new[] { new ApprovalRule(1, "SecurityReviewer", 1), new ApprovalRule(2, "ApiApprover", 1) }, CanonicalJson.Options);
            db.Add(release); AddSeats(db, api.ApprovalFlow.Id, release.Id);
        }
        // A direct non-production Ready release has no seats and does not belong to this inbox.
        var direct = Copy(original, api.Environment.Id); direct.Status = "Ready"; direct.ApprovalPolicy = "[]"; db.Add(direct);
        await db.SaveChangesAsync(); var actor = new ActorContext(reviewer.User.Id, "page");
        var pending = await Service(db).ListAsync(Filter(), actor, default);
        Assert.Equal(1, pending.Page.Total); Assert.Equal(mine, Assert.Single(pending.Page.Items).Id);
        Assert.Equal(new ApprovalInboxCounts(1, 0, 51), pending.Counts);
        var all = await Service(db).ListAsync(Filter("AllVisible"), actor, default);
        Assert.Equal(51, all.Page.Total); Assert.Equal(50, all.Page.Items.Count); Assert.DoesNotContain(all.Page.Items, x => x.Id == mine);
        var last = await Service(db).ListAsync(Filter("AllVisible", 2), actor, default);
        var row = Assert.Single(last.Page.Items); Assert.Equal(mine, row.Id); Assert.Equal(2, row.RequiredCount); Assert.Equal(0, row.ApprovedCount);
        Assert.Equal(all.Counts, last.Counts);
    }

    // Functional permissions are organization-specific even when scope grants span organizations.
    [Fact] public async Task AllOrganizationsRespectFunctionalAndScopeGrants()
    {
        await using var api = new ApiFixture(); var originalId = await SubmittedAsync(api);
        var reviewer = await api.NewReviewerAsync("ApiApprover"); await using var db = api.Context();
        var original = await db.Set<ReleaseRecord>().SingleAsync(r => r.Id == originalId);
        var org = new Organization { Code = "SECOND", Name = "第二组织" }; var project = new Project { OrganizationId = org.Id, Code = "P2", Name = "第二项目" };
        var env = new EnvironmentRecord { ProjectId = project.Id, Code = "PROD2", Name = "第二生产环境", IsProduction = true };
        var hiddenOrg = new Organization { Code = "HIDDEN", Name = "不得泄漏的组织" }; var hiddenProject = new Project { OrganizationId = hiddenOrg.Id, Code = "H", Name = "隐藏项目" };
        var hiddenEnv = new EnvironmentRecord { ProjectId = hiddenProject.Id, Code = "H", Name = "不得泄漏的环境" };
        db.AddRange(org, project, env, hiddenOrg, hiddenProject, hiddenEnv);
        var second = Copy(original, env.Id); var hidden = Copy(original, hiddenEnv.Id); db.AddRange(second, hidden);
        AddSeats(db, api.ApprovalFlow.Id, second.Id); AddSeats(db, api.ApprovalFlow.Id, hidden.Id);
        db.Add(new UserProjectScope { UserId = reviewer.User.Id, OrganizationId = org.Id, AccessMode = "read_write" });
        db.Add(new UserProjectScope { UserId = reviewer.User.Id, OrganizationId = hiddenOrg.Id, AccessMode = "read_write" });
        var role = new Role { OrganizationId = org.Id, Code = "ApiApprover", Name = "第二组织审核" }; db.Add(role);
        db.Add(new UserRole { UserId = reviewer.User.Id, RoleId = role.Id });
        var permissions = await db.Set<Permission>().Where(p => p.Code == "release.read" || p.Code == "approval.act").ToArrayAsync();
        foreach (var permission in permissions) db.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        var result = await Service(db).ListAsync(Filter(), new(reviewer.User.Id, "orgs"), default);
        Assert.Equal(2, result.Page.Total); Assert.Equal(2, result.Counts.AllVisible);
        Assert.Contains(result.Page.Items, x => x.Id == originalId); Assert.Contains(result.Page.Items, x => x.Id == second.Id);
        Assert.DoesNotContain("不得泄漏", JsonSerializer.Serialize(result, CanonicalJson.Options));
        var filtered = await Service(db).ListAsync(Filter() with { EnvironmentId = env.Id }, new(reviewer.User.Id, "filter"), default);
        var row = Assert.Single(filtered.Page.Items); Assert.Equal(org.Id, row.Organization.Id); Assert.Equal(project.Id, row.Project.Id); Assert.Equal(env.Id, row.Environment.Id);
    }

    [Fact] public async Task HandledHistoryRequiresCurrentReadScope()
    {
        await using var api = new ApiFixture(); var id = await SubmittedAsync(api); var reviewer = await api.NewReviewerAsync("ApiApprover", "HistoryReader");
        using var action = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{id}/approve", new { comment = "历史" }); action.EnsureSuccessStatusCode();
        await using var db = api.Context();
        var roleId = await db.Set<Role>().Where(r => r.Code == "ApiApprover").Select(r => r.Id).SingleAsync();
        await db.Set<UserRole>().Where(r => r.UserId == reviewer.User.Id && r.RoleId == roleId).ExecuteDeleteAsync();
        var actor = new ActorContext(reviewer.User.Id, "history"); var result = await Service(db).ListAsync(Filter("HandledMine"), actor, default);
        Assert.Equal(id, Assert.Single(result.Page.Items).Id); Assert.Equal(1, result.Counts.HandledMine);
        await db.Set<UserProjectScope>().Where(g => g.UserId == reviewer.User.Id).ExecuteDeleteAsync();
        var revoked = await Service(db).ListAsync(Filter("HandledMine"), actor, default);
        Assert.Empty(revoked.Page.Items); Assert.Equal(new ApprovalInboxCounts(0, 0, 0), revoked.Counts);
    }

    [Fact] public async Task RoleChangeMatchesA1Eligibility()
    {
        await using var api = new ApiFixture(); var id = await SubmittedAsync(api); var reviewer = await api.NewReviewerAsync("ApiApprover", "Reader");
        await using var db = api.Context(); var actor = new ActorContext(reviewer.User.Id, "role");
        Assert.Equal(1, (await Service(db).ListAsync(Filter(), actor, default)).Page.Total);
        var frozenRoleId = await db.Set<Role>().Where(r => r.Code == "ApiApprover").Select(r => r.Id).SingleAsync();
        await db.Set<UserRole>().Where(r => r.UserId == reviewer.User.Id && r.RoleId == frozenRoleId).ExecuteDeleteAsync();
        Assert.Equal(0, (await Service(db).ListAsync(Filter(), actor, default)).Page.Total);
        var visible = await Service(db).ListAsync(Filter("AllVisible"), actor, default); Assert.Equal(id, Assert.Single(visible.Page.Items).Id);
        Assert.False(visible.Page.Items[0].ApprovalEligibility.CanAct);
        using var action = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{id}/approve", new { comment = "撤角色" });
        Assert.Equal(HttpStatusCode.Forbidden, action.StatusCode);
    }

    // Risk permissions are independent of release visibility; restricted is not zero risk.
    [Fact] public async Task RestrictedRiskHasNoCountsOrComments()
    {
        await using var api = new ApiFixture(); var id = await SubmittedAsync(api); var reviewer = await api.NewReviewerAsync("ApiApprover");
        await using var db = api.Context(); var release = await db.Set<ReleaseRecord>().SingleAsync(r => r.Id == id);
        var candidate = JsonSerializer.Deserialize<FrozenReleaseCandidate>(release.CandidateBytes!, CanonicalJson.Options)!;
        var comparison = Guid.NewGuid(); var review = Guid.NewGuid();
        var summary = new RiskReviewSummaryDto(review, comparison, api.Api.Id, Guid.NewGuid(), api.Version.Id, "1", "2", "AcceptedRisk", api.User.Id,
            DateTimeOffset.UtcNow, "input", "report", "engine", "Partial", new(1, 2, 3, 4, 5, 6), "PRIVATE_REVIEW_COMMENT");
        release.CandidateBytes = CanonicalJson.Serialize(candidate with { RiskReviewReferences = [new(api.Api.Id, api.Version.Id, comparison, review, "input", "report", "engine", summary)] });
        await db.SaveChangesAsync();
        var restricted = await Service(db).ListAsync(Filter(), new(reviewer.User.Id, "restricted"), default);
        var risk = Assert.Single(restricted.Page.Items).Risk; Assert.Equal("Restricted", risk.Visibility); Assert.Null(risk.Counts); Assert.Null(risk.ReviewCount);
        var json = JsonSerializer.Serialize(restricted, CanonicalJson.Options);
        Assert.DoesNotContain("PRIVATE_REVIEW_COMMENT", json); Assert.DoesNotContain("frozenCandidate", json); Assert.DoesNotContain("secretHash", json); Assert.DoesNotContain("riskReviewReferences", json);
        var roleId = await db.Set<Role>().Where(r => r.Code == "ApiApprover").Select(r => r.Id).SingleAsync();
        foreach (var p in await db.Set<Permission>().Where(p => p.Code == "api.read" || p.Code == "api.version.read" || p.Code == "api.schema.read").ToArrayAsync())
            db.Add(new RolePermission { RoleId = roleId, PermissionId = p.Id });
        await db.SaveChangesAsync(); var readable = await Service(db).ListAsync(Filter(), new(reviewer.User.Id, "readable"), default);
        risk = Assert.Single(readable.Page.Items).Risk; Assert.Equal("Visible", risk.Visibility); Assert.Equal(new ComparisonCounts(1, 2, 3, 4, 5, 6), risk.Counts); Assert.Equal("Partial", risk.Coverage);
        Assert.DoesNotContain("PRIVATE_REVIEW_COMMENT", JsonSerializer.Serialize(readable, CanonicalJson.Options));
    }

    // Changing the first seat between counts and rows must not mix database snapshots.
    [Fact] public async Task CountsAndRowsUseSameSnapshot()
    {
        await using var api = new ApiFixture(); var id = await SubmittedAsync(api); var reviewer = await api.NewReviewerAsync("ApiApprover");
        var interceptor = new InboxInterceptor(async () => { using var action = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{id}/approve", new { comment = "并发办理" }); action.EnsureSuccessStatusCode(); });
        await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(api.Database.ConnectionString).AddInterceptors(interceptor).Options);
        var result = await Service(db).ListAsync(Filter(), new(reviewer.User.Id, "snapshot"), default);
        Assert.True(interceptor.Changed); Assert.Equal(1, result.Counts.PendingMine); Assert.Equal(1, result.Page.Total);
        Assert.True(Assert.Single(result.Page.Items).ApprovalEligibility.CanAct);
        await using var next = api.Context();
        Assert.Equal(0, (await Service(next).ListAsync(Filter(), new(reviewer.User.Id, "next"), default)).Counts.PendingMine);
    }

    [Theory]
    [InlineData("view=Other")]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("status=Invented")]
    [InlineData("environmentId=malformed")]
    [InlineData("page=abc")]
    [InlineData("view=PendingMine&view=AllVisible")]
    public async Task InvalidParametersReturn422(string query)
    {
        await using var api = new ApiFixture(); await SubmittedAsync(api);
        using var response = await api.Client.GetAsync("/api/v1/approvals?" + query); Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact] public async Task InvalidFilterDoesNotEchoForeignScope()
    {
        await using var api = new ApiFixture(); await SubmittedAsync(api); var reviewer = await api.NewReviewerAsync("ApiApprover");
        using var response = await reviewer.Client.GetAsync($"/api/v1/approvals?environmentId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.DoesNotContain("组织", await response.Content.ReadAsStringAsync());
        using var wrongHierarchy = await reviewer.Client.GetAsync($"/api/v1/approvals?environmentId={api.Environment.Id}&projectId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, wrongHierarchy.StatusCode);
        await using var db = api.Context(); await db.Set<UserProjectScope>().Where(s => s.UserId == reviewer.User.Id).ExecuteDeleteAsync();
        using var revoked = await reviewer.Client.GetAsync($"/api/v1/approvals?environmentId={api.Environment.Id}"); Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Fact] public async Task StatusFiltersOnlyRowsAndReceiptsAreNoStore()
    {
        await using var api = new ApiFixture(); await SubmittedAsync(api); var reviewer = await api.NewReviewerAsync("ApiApprover");
        using var response = await reviewer.Client.GetAsync("/api/v1/approvals?view=AllVisible&status=Rejected"); response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<ApprovalInboxPageDto>())!;
        Assert.Empty(result.Page.Items); Assert.Equal(0, result.Page.Total); Assert.Equal(new ApprovalInboxCounts(1, 0, 1), result.Counts);
        Assert.Equal("Rejected", result.Filter!.Status);
    }

    [Fact] public async Task InactiveSessionCannotReadInbox()
    {
        await using var api = new ApiFixture(); await SubmittedAsync(api); await using var db = api.Context();
        (await db.Set<UserRecord>().SingleAsync(u => u.Id == api.User.Id)).Status = "Disabled"; await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ApiException>(() => Service(db).ListAsync(Filter(), new(api.User.Id, "inactive"), default)); Assert.Equal(401, error.Status);
        using var response = await api.Client.GetAsync("/api/v1/approvals"); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A delayed real database read exercises the overall budget, not an artificial empty page.
    [Fact] public async Task QueryBudgetExpiresWithRetryableError()
    {
        await using var api = new ApiFixture(); await SubmittedAsync(api); var reviewer = await api.NewReviewerAsync("ApiApprover");
        await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(api.Database.ConnectionString).AddInterceptors(new SlowInboxInterceptor()).Options);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ApiException>(() => Service(db).ListAsync(Filter(), new(reviewer.User.Id, "timeout"), default));
        Assert.Equal(503, error.Status); Assert.Equal("approval_query_timeout", error.Code); Assert.InRange(started.Elapsed.TotalSeconds, 9, 13);
    }

    [Fact] public async Task BoundedPageUsesDatabasePaginationAndQueryPlan()
    {
        await using var api = new ApiFixture(); var id = await SubmittedAsync(api); var reviewer = await api.NewReviewerAsync("ApiApprover");
        await using (var seed = api.Context())
        {
            var original = await seed.Set<ReleaseRecord>().SingleAsync(r => r.Id == id);
            for (var i = 0; i < 600; i++) { var release = Copy(original, api.Environment.Id); seed.Add(release); AddSeats(seed, api.ApprovalFlow.Id, release.Id); }
            await seed.SaveChangesAsync();
        }
        var capture = new QueryCapture();
        await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(api.Database.ConnectionString).AddInterceptors(capture).Options);
        var actor = new ActorContext(reviewer.User.Id, "query-plan");
        var result = await Service(db).ListAsync(Filter(size: 100), actor, default);
        Assert.Equal(601, result.Page.Total); Assert.Equal(100, result.Page.Items.Count);
        Assert.InRange(capture.ReadCount, 1, 12); Assert.NotNull(capture.PageQuery);
        var pageReadCount = capture.ReadCount;
        Assert.Contains("LIMIT", capture.PageQuery!, StringComparison.OrdinalIgnoreCase);
        await using var connection = await api.Database.OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + capture.PageQuery, connection);
        foreach (var parameter in capture.PageParameters) explain.Parameters.Add(parameter);
        var plan = (string)(await explain.ExecuteScalarAsync())!;
        using var document = JsonDocument.Parse(plan); var root = document.RootElement[0];
        Assert.True(root.GetProperty("Execution Time").GetDouble() < 10000);
        Assert.Contains("Limit", plan); Assert.DoesNotContain("candidate_bytes", capture.PageQuery!);
        var beyond = await Service(db).ListAsync(Filter(page: int.MaxValue, size: 100), actor, default);
        Assert.Empty(beyond.Page.Items); Assert.Equal(601, beyond.Page.Total);
        Assert.False(db.Database.HasPendingModelChanges());
        output.WriteLine("APPROVAL_QUERY_PLAN=" + JsonSerializer.Serialize(new { rows = 601, pageSize = 100, reads = pageReadCount, readsIncludingBeyondPageCheck = capture.ReadCount, plan = root.Clone() }));
    }

    private static ReleaseRecord Copy(ReleaseRecord source, Guid environmentId) => new()
    { EnvironmentId = environmentId, ReleaseNo = "INBOX-" + Guid.NewGuid().ToString("N"), ReleaseType = source.ReleaseType,
        Status = source.Status, RequestedBy = source.RequestedBy, CandidateBytes = source.CandidateBytes, ApprovalPolicy = source.ApprovalPolicy };
    private static void AddSeats(WebApiDbContext db, Guid flow, Guid release)
    { db.Add(new ApprovalTask { ReleaseId = release, FlowId = flow, StepOrder = 1, Status = "Pending" }); db.Add(new ApprovalTask { ReleaseId = release, FlowId = flow, StepOrder = 2, Status = "Pending" }); }

    private sealed class InboxInterceptor(Func<Task> change) : DbCommandInterceptor
    {
        public bool Changed { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        { if (!Changed && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase)) { Changed = true; await change(); } return result; }
    }
    private sealed class SlowInboxInterceptor : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken); return result; }
    }
    private sealed class QueryCapture : DbCommandInterceptor
    {
        public int ReadCount { get; private set; }
        public string? PageQuery { get; private set; }
        public NpgsqlParameter[] PageParameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (command.CommandText.Contains("approval-inbox-bounded-page", StringComparison.Ordinal))
            {
                PageQuery = command.CommandText;
                PageParameters = command.Parameters.Cast<NpgsqlParameter>().Select(p => new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value }).ToArray();
            }
            return ValueTask.FromResult(result);
        }
    }
}

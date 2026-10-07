using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ApprovalEligibilityTests
{
    private static ScopeRef Scope(ApiFixture api) => new(api.Organization.Id, api.Project.Id, api.Environment.Id);
    private static ApprovalEligibilityService Service(WebApiDbContext db) => new(db, new AuthorizationService(db));

    // Selecting only the first (read-only) grant would incorrectly reject this user.
    [Fact] public async Task MixedScopeUsesWritableEnvironmentGrant()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedReleaseAsync();
        using var login = await api.LoginAsync(); var id = await api.CreateSubmittedReleaseAsync();
        var reviewer = await api.NewReviewerAsync("ApiApprover");
        await using var db = api.Context();
        var grant = await db.Set<UserProjectScope>().SingleAsync(g => g.UserId == reviewer.User.Id);
        grant.AccessMode = "read";
        db.Add(new UserProjectScope { UserId = reviewer.User.Id, OrganizationId = api.Organization.Id,
            ProjectId = api.Project.Id, EnvironmentId = api.Environment.Id, AccessMode = "read_write" });
        await db.SaveChangesAsync();
        var release = await db.Set<ReleaseRecord>().SingleAsync(r => r.Id == id);
        var result = await Service(db).EvaluateAsync(release, Scope(api), new(reviewer.User.Id, "mixed"), default);
        Assert.True(result.CanAct); Assert.Equal(1, result.CurrentStepOrder); Assert.Null(result.ReasonCode);
        using var approve = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{id}/approve", new { comment = "混合范围" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    // Administrator identity must never bypass applicant/independent-seat restrictions.
    [Fact] public async Task ApplicantAndUsedSeatAlwaysRejected()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedReleaseAsync();
        using var login = await api.LoginAsync(); var id = await api.CreateSubmittedReleaseAsync();
        var reviewer = await api.NewReviewerAsync("ApiApprover", "SecurityReviewer");
        await using var db = api.Context();
        var admin = new Role { Code = "PlatformAdmin", IsSystem = true, Name = "系统管理员" }; db.Add(admin);
        db.Add(new UserRole { UserId = api.User.Id, RoleId = admin.Id });
        db.Add(new UserRole { UserId = reviewer.User.Id, RoleId = admin.Id }); await db.SaveChangesAsync();
        var release = await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r => r.Id == id);
        var applicant = await Service(db).EvaluateAsync(release, Scope(api), new(api.User.Id, "self"), default);
        Assert.False(applicant.CanAct); Assert.Equal("self_approval", applicant.ReasonCode);
        var error = await Assert.ThrowsAsync<ApiException>(() => Service(db).RequireTaskAsync(release, Scope(api), new(api.User.Id, "self"), default));
        Assert.Equal(403, error.Status); Assert.Equal("self_approval", error.Code);
        using var first = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{id}/approve", new { comment = "第一席" });
        first.EnsureSuccessStatusCode();
        var used = await Service(db).EvaluateAsync(release, Scope(api), new(reviewer.User.Id, "used"), default);
        Assert.False(used.CanAct); Assert.Equal("independent_approval_required", used.ReasonCode);
        using var again = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{id}/approve", new { comment = "重复席" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    // One completed first-step seat cannot advance a multi-seat first step.
    [Fact] public async Task ConcurrentSeatsDoNotExposeStepTwoEarly()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedReleaseAsync();
        await using (var db = api.Context()) { (await db.Set<ApprovalStep>().SingleAsync(s => s.StepOrder == 1)).RequiredCount = 2; await db.SaveChangesAsync(); }
        using var login = await api.LoginAsync(); var id = await api.CreateSubmittedReleaseAsync();
        var first = await api.NewReviewerAsync("ApiApprover"); var second = await api.NewReviewerAsync("ApiApprover");
        var security = await api.NewReviewerAsync("SecurityReviewer");
        using var approved = await ApiFixture.CommandAsync(first.Client, $"/api/v1/releases/{id}/approve", new { comment = "席一" }); approved.EnsureSuccessStatusCode();
        await using var check = api.Context(); var release = await check.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r => r.Id == id);
        var result = await Service(check).EvaluateAsync(release, Scope(api), new(security.User.Id, "security"), default);
        Assert.Equal(1, result.CurrentStepOrder); Assert.False(result.CanAct); Assert.Equal("approval_role_required", result.ReasonCode);
        using var early = await ApiFixture.CommandAsync(security.Client, $"/api/v1/releases/{id}/approve", new { comment = "不能提前" });
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        using var complete = await ApiFixture.CommandAsync(second.Client, $"/api/v1/releases/{id}/approve", new { comment = "席二" }); complete.EnsureSuccessStatusCode();
        result = await Service(check).EvaluateAsync(release, Scope(api), new(security.User.Id, "security"), default);
        Assert.True(result.CanAct); Assert.Equal(2, result.CurrentStepOrder);
    }

    // Adding release.read to the existing action contract would break this actor.
    [Fact] public async Task ApprovalActionWithoutReleaseReadKeepsExistingContract()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedReleaseAsync();
        using var login = await api.LoginAsync(); var id = await api.CreateSubmittedReleaseAsync();
        var reviewer = await api.NewReviewerAsync("ApiApprover");
        await using var db = api.Context();
        var readId = await db.Set<Permission>().Where(p => p.Code == "release.read").Select(p => p.Id).SingleAsync();
        var roleId = await db.Set<UserRole>().Where(u => u.UserId == reviewer.User.Id).Select(u => u.RoleId).SingleAsync();
        await db.Set<RolePermission>().Where(p => p.RoleId == roleId && p.PermissionId == readId).ExecuteDeleteAsync();
        var release = await db.Set<ReleaseRecord>().SingleAsync(r => r.Id == id);
        Assert.True((await Service(db).EvaluateAsync(release, Scope(api), new(reviewer.User.Id, "act"), default)).CanAct);
        using var get = await reviewer.Client.GetAsync($"/api/v1/releases/{id}"); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var approve = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{id}/approve", new { comment = "仅办理权限" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    // Functional permission and frozen-role qualification can come from separate roles.
    [Fact] public async Task ComposableQueryMatchesCommandEligibility()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedReleaseAsync();
        using var login = await api.LoginAsync(); var id = await api.CreateSubmittedReleaseAsync();
        var reviewer = await api.NewReviewerAsync("ApiApprover", "ActionCarrier");
        await using var db = api.Context(); var actor = new ActorContext(reviewer.User.Id, "query");
        var frozenRoleId = await db.Set<Role>().Where(r => r.Code == "ApiApprover").Select(r => r.Id).SingleAsync();
        await db.Set<RolePermission>().Where(p => p.RoleId == frozenRoleId).ExecuteDeleteAsync();
        var release = await db.Set<ReleaseRecord>().SingleAsync(r => r.Id == id);
        var service = Service(db);
        Assert.True(await service.QueryActionableReleaseIds(actor, [api.Environment.Id]).AnyAsync(r => r == id));
        Assert.False(await service.QueryActionableReleaseIds(actor, [Guid.NewGuid()]).AnyAsync());
        Assert.True((await service.EvaluateAsync(release, Scope(api), actor, default)).CanAct);
        using var approve = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{id}/approve", new { comment = "权限并集" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.False(await service.QueryActionableReleaseIds(actor, [api.Environment.Id]).AnyAsync());
    }

    [Theory]
    [InlineData("role", "permission_denied", 403)]
    [InlineData("scope", "scope_denied", 403)]
    [InlineData("inactiveEnvironment", "inactive_scope", 409)]
    [InlineData("inactiveUser", "inactive_session", 401)]
    public async Task RevocationAndInactiveScopesCannotAct(string change, string code, int status)
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedReleaseAsync();
        using var login = await api.LoginAsync(); var id = await api.CreateSubmittedReleaseAsync();
        var reviewer = await api.NewReviewerAsync("ApiApprover"); await using var db = api.Context();
        switch (change)
        {
            case "role": await db.Set<UserRole>().Where(r => r.UserId == reviewer.User.Id).ExecuteDeleteAsync(); break;
            case "scope": await db.Set<UserProjectScope>().Where(r => r.UserId == reviewer.User.Id).ExecuteDeleteAsync(); break;
            case "inactiveEnvironment": (await db.Set<EnvironmentRecord>().SingleAsync()).Status = "Inactive"; break;
            case "inactiveUser": (await db.Set<UserRecord>().SingleAsync(u => u.Id == reviewer.User.Id)).Status = "Inactive"; break;
        }
        await db.SaveChangesAsync(); var release = await db.Set<ReleaseRecord>().SingleAsync(r => r.Id == id);
        var service = Service(db); var actor = new ActorContext(reviewer.User.Id, "revoked");
        Assert.False(await service.QueryActionableReleaseIds(actor, [api.Environment.Id]).AnyAsync());
        if (status == 401)
        { var e = await Assert.ThrowsAsync<ApiException>(() => service.EvaluateAsync(release, Scope(api), actor, default)); Assert.Equal(status, e.Status); Assert.Equal(code, e.Code); }
        else { var result = await service.EvaluateAsync(release, Scope(api), actor, default); Assert.False(result.CanAct); Assert.Equal(code, result.ReasonCode); }
        var error = await Assert.ThrowsAsync<ApiException>(() => service.RequireTaskAsync(release, Scope(api), actor, default));
        Assert.Equal(status, error.Status); Assert.Equal(code, error.Code);
        Assert.False(await db.Set<ApprovalTask>().AnyAsync(t => t.Status == "Approved"));
    }

    [Fact] public async Task ReadDtoExposesQualificationWithoutChangingFrozenHash()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedReleaseAsync();
        using var login = await api.LoginAsync(); var id = await api.CreateSubmittedReleaseAsync();
        var reviewer = await api.NewReviewerAsync("ApiApprover");
        var mine = await reviewer.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{id}");
        Assert.NotNull(mine!.ApprovalEligibility); Assert.True(mine.ApprovalEligibility.CanAct);
        var applicant = await api.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{id}");
        Assert.NotNull(applicant!.ApprovalEligibility); Assert.False(applicant.ApprovalEligibility.CanAct);
        Assert.Equal("self_approval", applicant.ApprovalEligibility.ReasonCode); Assert.Equal(mine.CandidateHash, applicant.CandidateHash);
    }
}

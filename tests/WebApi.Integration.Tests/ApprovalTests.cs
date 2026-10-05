using System.Net;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ApprovalTests
{
    [Fact] public async Task DraftPreviewSupportsReviewWithoutFreezingOrLeakingCredentials()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();
        using var draft=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/environments/{s.Api.Environment.Id}/releases",await s.Api.PreviewReleaseRequestAsync());draft.EnsureSuccessStatusCode();var id=(await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<System.Text.Json.JsonElement>(draft.Content)).GetProperty("id").GetGuid();
        using var preview=await s.Api.Client.GetAsync($"/api/v1/releases/{id}/preview");Assert.Equal(HttpStatusCode.OK,preview.StatusCode);var json=await preview.Content.ReadAsStringAsync();Assert.Contains("/orders",json);Assert.Contains("credentials",json);Assert.DoesNotContain("secretHash",json,StringComparison.OrdinalIgnoreCase);
        await using var db=s.Api.Context();var release=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id);Assert.Equal("Draft",release.Status);Assert.Null(release.ApprovalPolicy);Assert.False(await db.Set<ApprovalTask>().AnyAsync());
    }
    [Fact] public async Task ApplicantCannotApprove()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();using var login=await api.LoginAsync();var id=await api.CreateSubmittedReleaseAsync();
        using var result=await ApiFixture.CommandAsync(api.Client,$"/api/v1/releases/{id}/approve",new {comment="自批"});Assert.Equal(HttpStatusCode.Forbidden,result.StatusCode);
        await using var db=api.Context();Assert.False(await db.Set<ApprovalTask>().AnyAsync(t=>t.Status=="Approved"));
    }
    [Fact] public async Task OnePersonCannotFillTwoSteps()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();using var login=await api.LoginAsync();var id=await api.CreateSubmittedReleaseAsync();var reviewer=await api.NewReviewerAsync("ApiApprover","SecurityReviewer");
        using var first=await ApiFixture.CommandAsync(reviewer.Client,$"/api/v1/releases/{id}/approve",new {comment="API批准"});Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        using var second=await ApiFixture.CommandAsync(reviewer.Client,$"/api/v1/releases/{id}/approve",new {comment="安全批准"});Assert.Equal(HttpStatusCode.Forbidden,second.StatusCode);
        var independent=await api.NewReviewerAsync("SecurityReviewer");using var last=await ApiFixture.CommandAsync(independent.Client,$"/api/v1/releases/{id}/approve",new {comment="独立安全批准"});Assert.Equal(HttpStatusCode.OK,last.StatusCode);
        await using var db=api.Context();Assert.Equal("Ready",(await db.Set<ReleaseRecord>().SingleAsync()).Status);Assert.Equal(2,await db.Set<ApprovalTask>().CountAsync(t=>t.Status=="Approved"));
    }
    [Fact] public async Task CandidateDoesNotChangeAfterSubmit()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();using var login=await api.LoginAsync();var id=await api.CreateSubmittedReleaseAsync();byte[] original;
        await using(var db=api.Context()) {original=(await db.Set<ReleaseRecord>().SingleAsync()).CandidateBytes!;var v=await db.Set<ApiVersion>().SingleAsync();v.Revision++;v.ChangeType="breaking";await db.SaveChangesAsync();}
        await using var check=api.Context();Assert.Equal(original,(await check.Set<ReleaseRecord>().SingleAsync()).CandidateBytes);Assert.Contains("compatible",System.Text.Encoding.UTF8.GetString(original));
    }
    [Fact] public async Task RoleRevokedImmediatelyBeforeApprovalBlocksWrite()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();using var login=await api.LoginAsync();var id=await api.CreateSubmittedReleaseAsync();var reviewer=await api.NewReviewerAsync("ApiApprover");await using(var db=api.Context()) {await db.Set<UserRole>().Where(r=>r.UserId==reviewer.User.Id).ExecuteDeleteAsync();}
        using var result=await ApiFixture.CommandAsync(reviewer.Client,$"/api/v1/releases/{id}/approve",new {comment="旧会话"});Assert.Equal(HttpStatusCode.Forbidden,result.StatusCode);
        await using var check=api.Context();Assert.False(await check.Set<ApprovalTask>().AnyAsync(t=>t.Status=="Approved"));
    }
    [Fact] public async Task RepeatedRejectOrCancelCannotRestoreReady()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();using var login=await api.LoginAsync();var id=await api.CreateSubmittedReleaseAsync();var reviewer=await api.NewReviewerAsync("ApiApprover");
        using var reject=await ApiFixture.CommandAsync(reviewer.Client,$"/api/v1/releases/{id}/reject",new {comment="拒绝"});Assert.Equal(HttpStatusCode.OK,reject.StatusCode);
        using var approval=await ApiFixture.CommandAsync(reviewer.Client,$"/api/v1/releases/{id}/approve",new {comment="重试"});Assert.Equal(HttpStatusCode.Conflict,approval.StatusCode);
        using var cancel=await ApiFixture.CommandAsync(api.Client,$"/api/v1/releases/{id}/cancel");Assert.Equal(HttpStatusCode.Conflict,cancel.StatusCode);
        await using var db=api.Context();Assert.Equal("Rejected",(await db.Set<ReleaseRecord>().SingleAsync()).Status);
    }
    [Fact] public async Task ApprovalPolicyDoesNotChangeAfterSubmission()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();using var login=await api.LoginAsync();var id=await api.CreateSubmittedReleaseAsync();await api.NewReviewerAsync("ApiApprover","SecurityReviewer");string? before;
        await using(var db=api.Context()) before=(await db.Set<ReleaseRecord>().SingleAsync()).ApprovalPolicy;
        using var update=await api.WriteAsync(HttpMethod.Put,$"/api/v1/approval-flows/{api.ApprovalFlow.Id}",new {name="改变模板",enabled=true,steps=new[]{new {stepOrder=1,roleCode="SecurityReviewer",requiredCount=1},new {stepOrder=2,roleCode="ApiApprover",requiredCount=1}}},"\"1\"");Assert.Equal(HttpStatusCode.OK,update.StatusCode);
        await using var check=api.Context();Assert.Equal(before,(await check.Set<ReleaseRecord>().SingleAsync()).ApprovalPolicy);Assert.Equal(2,(await check.Set<ApprovalFlow>().SingleAsync()).Revision);
    }
}

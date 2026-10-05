using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PolicyReleaseTests
{
    internal static async Task<ApiFixture> Prepare()
    {
        var f=new ApiFixture();await f.InitializeAsync();await f.SeedReleaseAsync();await using var db=f.Context();var role=await db.Set<UserRole>().Where(r=>r.UserId==f.User.Id).Select(r=>r.RoleId).SingleAsync();foreach(var code in new[]{"policy.read","policy.write"}) {var p=new Permission {Code=code,Name=code,Module="policy"};db.Add(p);db.Add(new RolePermission {RoleId=role,PermissionId=p.Id});}await db.SaveChangesAsync();using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();
        using var route=await f.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{f.Environment.Id}/routes",f.RouteBody("/orders"));route.EnsureSuccessStatusCode();var routeId=(await route.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var policy=await f.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{f.Project.Id}/policies",new SavePolicyRequest("共享超时","timeout","{\"timeoutMs\":1000}"));policy.EnsureSuccessStatusCode();var dto=(await policy.Content.ReadFromJsonAsync<PolicyDto>())!;
        using var bind=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{routeId}/policies",new SaveRoutePoliciesRequest([new(dto.Id,0)]),"\"1\"");bind.EnsureSuccessStatusCode();return f;
    }
    internal static async Task<FrozenCandidateView> Preview(ApiFixture f)
    {using var response=await ApiFixture.CommandAsync(f.Client,$"/api/v1/environments/{f.Environment.Id}/releases/preview",new {baseConfigVersion=0L,versionIds=new[]{f.Version.Id}});response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<FrozenCandidateView>())!;}
    private static CreateReleaseRequest Input(ApiFixture f,FrozenCandidateView p)=>new(0,[f.Version.Id],p.ResourceRevisions);
    private static async Task<ReleaseDto> Create(ApiFixture f,CreateReleaseRequest input)
    {using var response=await ApiFixture.CommandAsync(f.Client,$"/api/v1/environments/{f.Environment.Id}/releases",input);response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<ReleaseDto>())!;}
    [Fact] public async Task PreviewIncludesEveryResourceRevision()
    {await using var f=await Prepare();var p=await Preview(f);Assert.Contains(p.ResourceRevisions,r=>r.Type=="policy");Assert.Contains(p.ResourceRevisions,r=>r.Type=="route");Assert.Contains(p.ResourceRevisions,r=>r.Type=="destination");Assert.Single(p.Policies!);Assert.Single(p.Bindings!);Assert.DoesNotContain("secretHash",JsonSerializer.Serialize(p,CanonicalJson.Options),StringComparison.OrdinalIgnoreCase);}
    [Fact] public async Task MissingPolicyOrRouteRevisionRejected()
    {await using var f=await Prepare();var p=await Preview(f);foreach(var type in new[]{"policy","route"}) {using var response=await ApiFixture.CommandAsync(f.Client,$"/api/v1/environments/{f.Environment.Id}/releases",Input(f,p) with {ResourceRevisions=p.ResourceRevisions.Where(r=>r.Type!=type).ToArray()});Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}}
    [Fact] public async Task ChangedBindingAfterPreviewReturns412()
    {await using var f=await Prepare();var p=await Preview(f);var r=p.Routes.Single();using var bind=await f.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{r.Id}/policies",new SaveRoutePoliciesRequest([]),$"\"{r.Revision}\"");bind.EnsureSuccessStatusCode();using var response=await ApiFixture.CommandAsync(f.Client,$"/api/v1/environments/{f.Environment.Id}/releases",Input(f,p));Assert.Equal(HttpStatusCode.PreconditionFailed,response.StatusCode);}
    [Fact] public async Task PostSubmitEditDoesNotChangeFrozenHash()
    {await using var f=await Prepare();var p=await Preview(f);var r=await Create(f,Input(f,p));using var submit=await ApiFixture.CommandAsync(f.Client,$"/api/v1/releases/{r.Id}/submit");submit.EnsureSuccessStatusCode();var frozen=(await submit.Content.ReadFromJsonAsync<ReleaseDto>())!;var policy=p.Policies!.Single();using var edit=await f.WriteAsync(HttpMethod.Put,$"/api/v1/policies/{policy.Id}",new SavePolicyRequest("共享超时","timeout","{\"timeoutMs\":2000}"),"\"1\"");edit.EnsureSuccessStatusCode();var after=await f.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{r.Id}");Assert.Equal(frozen.CandidateHash,after!.CandidateHash);Assert.Contains("1000",after.FrozenCandidate!.Policies!.Single().Config);}
    [Fact] public async Task ExistingFullRevisionDraftSubmitsUnchanged()
    {await using var f=await Prepare();var p=await Preview(f);var r=await Create(f,Input(f,p));using var submit=await ApiFixture.CommandAsync(f.Client,$"/api/v1/releases/{r.Id}/submit");submit.EnsureSuccessStatusCode();Assert.Equal("WaitingApproval",(await submit.Content.ReadFromJsonAsync<ReleaseDto>())!.State);}
    [Fact] public async Task StaleDraftReviewDoesNotSilentlyRefresh()
    {await using var f=await Prepare();var p=await Preview(f);var r=await Create(f,Input(f,p));byte[] before;await using(var db=f.Context()) {before=(await db.Set<ReleaseRecord>().SingleAsync()).CandidateBytes!;var policy=await db.Set<Policy>().SingleAsync();policy.Config="{\"timeoutMs\":2000}";policy.VersionNo++;await db.SaveChangesAsync();}var review=await f.Client.GetFromJsonAsync<FrozenCandidateView>($"/api/v1/releases/{r.Id}/preview");Assert.False(review!.PreconditionsCurrent);await using(var db=f.Context()) Assert.Equal(before,(await db.Set<ReleaseRecord>().SingleAsync()).CandidateBytes);using var failed=await ApiFixture.CommandAsync(f.Client,$"/api/v1/releases/{r.Id}/submit");Assert.Equal(HttpStatusCode.PreconditionFailed,failed.StatusCode);using var refreshed=await ApiFixture.CommandAsync(f.Client,$"/api/v1/releases/{r.Id}/refresh-preconditions",new {resourceRevisions=review!.ResourceRevisions});refreshed.EnsureSuccessStatusCode();using var submit=await ApiFixture.CommandAsync(f.Client,$"/api/v1/releases/{r.Id}/submit");submit.EnsureSuccessStatusCode();}
    [Fact] public async Task IncompleteLegacyDraftCanBeReviewedAndExplicitlyRefreshed()
    {await using var f=await Prepare();var p=await Preview(f);var r=await Create(f,Input(f,p));await using(var db=f.Context()) {var row=await db.Set<ReleaseRecord>().SingleAsync();row.CandidateBytes=CanonicalJson.Serialize(new CreateReleaseRequest(0,[f.Version.Id],[new("version",f.Version.Id,1)]));await db.SaveChangesAsync();}var preview=await f.Client.GetFromJsonAsync<FrozenCandidateView>($"/api/v1/releases/{r.Id}/preview");Assert.False(preview!.PreconditionsCurrent);using var failed=await ApiFixture.CommandAsync(f.Client,$"/api/v1/releases/{r.Id}/submit");Assert.Equal(HttpStatusCode.UnprocessableEntity,failed.StatusCode);using var refresh=await ApiFixture.CommandAsync(f.Client,$"/api/v1/releases/{r.Id}/refresh-preconditions",new {resourceRevisions=preview!.ResourceRevisions});refresh.EnsureSuccessStatusCode();}
}

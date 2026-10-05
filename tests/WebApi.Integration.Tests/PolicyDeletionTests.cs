using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PolicyDeletionTests
{
    [Fact] public async Task ProtectedReferencesBlockDeletionWithoutDisclosure()
    {await using var f=await PolicyReleaseTests.Prepare();var p=await PolicyReleaseTests.Preview(f);var policy=p.Policies!.Single();using var denied=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/policies/{policy.Id}",etag:"\"1\"");Assert.Equal(HttpStatusCode.Conflict,denied.StatusCode);Assert.DoesNotContain("orders",await denied.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);}
    [Theory] [InlineData("WaitingApproval","publish")] [InlineData("Ready","publish")] [InlineData("Building","publish")] [InlineData("Publishing","publish")] [InlineData("Draft","rollback")]
    public async Task EveryFrozenInProgressStateProtectsDeletion(string state,string releaseType)
    {
        await using var f=await PolicyReleaseTests.Prepare();var view=await PolicyReleaseTests.Preview(f);var policy=view.Policies!.Single();
        await using(var db=f.Context()) {await db.Set<RoutePolicyBinding>().ExecuteDeleteAsync();var candidate=new FrozenReleaseCandidate(f.Environment.Id,f.Organization.Id,f.Project.Id,0,[f.Version.Id],view.Versions,view.Routes,view.Clusters,view.Policies!,view.Bindings!,[],view.ResourceRevisions);db.Add(new ReleaseRecord {EnvironmentId=f.Environment.Id,ReleaseNo="秘密发布",Status=state,ReleaseType=releaseType,RequestedBy=f.User.Id,CandidateBytes=CanonicalJson.Serialize(candidate),ApprovalPolicy=releaseType=="rollback"?null:"[]"});await db.SaveChangesAsync();}
        using var denied=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/policies/{policy.Id}",etag:"\"1\"");Assert.Equal(HttpStatusCode.Conflict,denied.StatusCode);Assert.DoesNotContain("秘密发布",await denied.Content.ReadAsStringAsync());
    }
    [Theory] [InlineData(true)] [InlineData(false)] public async Task DesiredAndActuallyAppliedSnapshotsProtectDeletion(bool desired)
    {
        await using var f=await PolicyReleaseTests.Prepare();Guid id;await using(var db=f.Context()) {id=(await db.Set<Policy>().SingleAsync()).Id;await db.Set<RoutePolicyBinding>().ExecuteDeleteAsync();var v=new GatewayConfigVersion {EnvironmentId=f.Environment.Id,VersionNo=1,CreatedBy=f.User.Id};db.Add(v);db.Add(new GatewayConfigSnapshot {ConfigVersionId=v.Id,PayloadBytes=CanonicalJson.Serialize(new RuntimeSnapshot("2.0",f.Environment.Id,1,DateTimeOffset.UtcNow,[],[],[new(id,"timeout","{\"timeoutMs\":1000}")],[]))});if(desired) (await db.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion=1;else db.Add(new GatewayNode {EnvironmentId=f.Environment.Id,NodeName="已停用旧实例",InstanceId=Guid.NewGuid().ToString(),Enabled=false,CurrentConfigVersion=1});await db.SaveChangesAsync();}using var denied=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/policies/{id}",etag:"\"1\"");Assert.Equal(HttpStatusCode.Conflict,denied.StatusCode);
    }
    [Fact] public async Task HistoryOnlyDeletionStillAllowsRollback()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();Guid policyId;
        await using(var db=s.Api.Context()) {var p=new Policy {OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,Name="历史超时",Type="timeout",Config="{\"timeoutMs\":1000}"};policyId=p.Id;db.Add(p);db.Add(new RoutePolicyBinding {RouteId=(await db.Set<ApiRoute>().SingleAsync()).Id,PolicyId=p.Id});var role=await db.Set<UserRole>().Where(r=>r.UserId==s.Api.User.Id).Select(r=>r.RoleId).SingleAsync();foreach(var code in new[]{"policy.read","policy.write"}) {var permission=new Permission {Code=code,Name=code,Module="policy"};db.Add(permission);db.Add(new RolePermission {RoleId=role,PermissionId=permission.Id});}await db.SaveChangesAsync();}
        await s.PublishAsync();await using(var db=s.Api.Context()) {await db.Set<RoutePolicyBinding>().ExecuteDeleteAsync();}var latest=await s.PublishAsync();using var deleted=await s.Api.WriteAsync(HttpMethod.Delete,$"/api/v1/policies/{policyId}",etag:"\"1\"");Assert.Equal(HttpStatusCode.NoContent,deleted.StatusCode);
        using var rollback=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{latest}/rollback",new {targetConfigVersion=1L});rollback.EnsureSuccessStatusCode();var id=(await rollback.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;using var submitted=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/submit");submitted.EnsureSuccessStatusCode();await s.ApproveAsync(id);await s.BuildAsync(id);await s.AckAllAsync(id);var artifact=await s.DesiredAsync(id);Assert.Equal(1,artifact.Envelope.ConfigVersion);Assert.Contains("1000",System.Text.Encoding.UTF8.GetString(artifact.Payload));
    }
}

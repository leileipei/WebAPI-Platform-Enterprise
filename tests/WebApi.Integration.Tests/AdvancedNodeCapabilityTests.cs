using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class AdvancedNodeCapabilityTests
{
    private static async Task Retry(DeploymentScenario s)
    {
        await using var db=s.Api.Context(); var route=await db.Set<ApiRoute>().SingleAsync();
        var policy=new Policy {OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,Name="有界重试",Type="retry",Config="""{"maxAttempts":2,"perAttemptTimeoutMs":3000,"baseDelayMs":50,"maxDelayMs":500,"jitterPercent":20,"retryStatusCodes":[502,503,504],"retryConnectionFailures":true}"""}; db.Add(policy); db.Add(new RoutePolicyBinding {RouteId=route.Id,PolicyId=policy.Id}); route.Revision++; await db.SaveChangesAsync();
    }
    private static async Task Capable(DeploymentScenario s)
    {
        for(var i=0;i<2;i++) { using var response=await s.NodeAsync(i,"/internal/v1/nodes/register",new RegisterNodeRequest(s.Api.Environment.Id,"node-"+i,s.Instances[i],"untrusted-app-version",["2.0","2.1","2.2"])); response.EnsureSuccessStatusCode(); }
    }
    private static async Task Unchanged(DeploymentScenario s)
    {
        await using var db=s.Api.Context(); var env=await db.Set<EnvironmentRecord>().SingleAsync(); Assert.Null(env.DesiredConfigVersion); Assert.Equal(0,env.DeploymentSequence); Assert.False(await db.Set<GatewayConfigVersion>().AnyAsync()); Assert.False(await db.Set<OutboxMessage>().AnyAsync());
    }
    [Fact]
    public async Task NodesAdvertise22BoundToTheirCurrentInstance()
    {
        await using var s=new DeploymentScenario(); await s.InitializeAsync(); await Capable(s);
        await using(var db=s.Api.Context()) foreach(var node in await db.Set<GatewayNode>().ToArrayAsync()) Assert.Contains("2.2",SnapshotSchemaCapabilities.Read(node));
        using var replacement=await s.NodeAsync(0,"/internal/v1/nodes/register",new RegisterNodeRequest(s.Api.Environment.Id,"node-0",Guid.NewGuid(),"2.2-marketing-version",["2.0","2.1"])); replacement.EnsureSuccessStatusCode();
        await using var check=s.Api.Context(); Assert.DoesNotContain("2.2",SnapshotSchemaCapabilities.Read(await check.Set<GatewayNode>().SingleAsync(n=>n.Id==s.NodeIds[0])));
    }
    [Fact]
    public async Task Missing22CapabilityDoesNotAdvanceDesiredOrSequence()
    {
        await using var s=new DeploymentScenario(); await s.InitializeAsync(); await Retry(s);
        using var preview=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/environments/{s.Api.Environment.Id}/releases/preview",new {baseConfigVersion=0L,versionIds=new[]{s.Api.Version.Id}});
        Assert.Equal(HttpStatusCode.Conflict,preview.StatusCode); Assert.Contains("gateway_schema_unsupported",await preview.Content.ReadAsStringAsync()); await Unchanged(s);
    }
    [Fact]
    public async Task SupportedPreviewAndThenSubmitRecheckCurrentInstance()
    {
        await using var s=new DeploymentScenario(); await s.InitializeAsync(); await Retry(s); await Capable(s);
        using var created=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/environments/{s.Api.Environment.Id}/releases",await s.Api.PreviewReleaseRequestAsync()); created.EnsureSuccessStatusCode(); var id=(await created.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
        using var replacement=await s.NodeAsync(0,"/internal/v1/nodes/register",new RegisterNodeRequest(s.Api.Environment.Id,"node-0",Guid.NewGuid(),"test",["2.0","2.1"])); replacement.EnsureSuccessStatusCode();
        using var submit=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/submit"); Assert.Equal(HttpStatusCode.Conflict,submit.StatusCode); Assert.Contains("gateway_schema_unsupported",await submit.Content.ReadAsStringAsync()); await Unchanged(s);
        await using var check=s.Api.Context(); Assert.Equal("Draft",(await check.Set<ReleaseRecord>().SingleAsync()).Status);
    }
    [Fact]
    public async Task WorkerRechecks22AfterQueueAndPreservesDesired()
    {
        await using var s=new DeploymentScenario(); await s.InitializeAsync(); await Retry(s); await Capable(s); var id=await s.ReadyAsync();
        using var publish=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/publish"); publish.EnsureSuccessStatusCode();
        using var replacement=await s.NodeAsync(0,"/internal/v1/nodes/register",new RegisterNodeRequest(s.Api.Environment.Id,"node-0",Guid.NewGuid(),"test",["2.0","2.1"])); replacement.EnsureSuccessStatusCode(); await s.BuildQueuedAsync(); await Unchanged(s);
        await using var db=s.Api.Context(); var release=await db.Set<ReleaseRecord>().SingleAsync(); Assert.Equal("Failed",release.Status); Assert.Equal("gateway_schema_unsupported",release.FailureCode);
    }
    [Fact]
    public async Task RollbackAndRetryOf22RequireAllEnabledTargets()
    {
        await using var s=new DeploymentScenario(); await s.InitializeAsync(); await Retry(s); await Capable(s); var first=await s.PublishAsync(false);
        await using(var db=s.Api.Context()) { (await db.Set<ReleaseRecord>().SingleAsync()).Status="Failed"; await db.SaveChangesAsync(); }
        using(var old=await s.NodeAsync(0,"/internal/v1/nodes/register",new RegisterNodeRequest(s.Api.Environment.Id,"node-0",s.Instances[0],"test",["2.0","2.1"]))) old.EnsureSuccessStatusCode();
        using(var retry=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{first}/retry")) Assert.Equal(HttpStatusCode.Conflict,retry.StatusCode);
        await Capable(s); await using(var db=s.Api.Context()) { await db.Set<RoutePolicyBinding>().ExecuteDeleteAsync(); }
        var second=await s.PublishAsync();
        using(var old=await s.NodeAsync(0,"/internal/v1/nodes/register",new RegisterNodeRequest(s.Api.Environment.Id,"node-0",s.Instances[0],"test",["2.0","2.1"]))) old.EnsureSuccessStatusCode();
        using var rollback=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{second}/rollback",new {targetConfigVersion=1L}); Assert.Equal(HttpStatusCode.Conflict,rollback.StatusCode); Assert.Contains("gateway_schema_unsupported",await rollback.Content.ReadAsStringAsync());
        await using var check=s.Api.Context(); Assert.Equal(2,(await check.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion); Assert.Equal(2,(await check.Set<EnvironmentRecord>().SingleAsync()).DeploymentSequence); Assert.Equal(2,await check.Set<ReleaseRecord>().CountAsync());
    }
}

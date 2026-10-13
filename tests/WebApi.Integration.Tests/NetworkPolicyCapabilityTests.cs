using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class NetworkPolicyCapabilityTests
{
    private const string Cors="""{"allowedOrigins":["https://portal.example.com"],"allowCredentials":false,"allowedMethods":["GET"],"allowedHeaders":[],"exposedHeaders":[],"maxAgeSeconds":600}""";
    private static async Task BindAsync(DeploymentScenario scenario)
    {
        await using var db=scenario.Api.Context();var route=await db.Set<ApiRoute>().SingleAsync();
        var policy=new Policy {OrganizationId=scenario.Api.Organization.Id,ProjectId=scenario.Api.Project.Id,Name="network",Type="cors",Config=Cors};db.Add(policy);db.Add(new RoutePolicyBinding {RouteId=route.Id,PolicyId=policy.Id});route.Revision++;await db.SaveChangesAsync();
    }
    private static async Task CapableAsync(DeploymentScenario s,int i,Guid? instance=null,bool network=true)
    {
        var schemas=network?new[]{"2.0","2.1","2.2","2.3"}:new[]{"2.0","2.1","2.2"};
        using var response=await s.NodeAsync(i,"/internal/v1/nodes/register",new RegisterNodeRequest(s.Api.Environment.Id,"node-"+i,instance??s.Instances[i],"untrusted-version",schemas));response.EnsureSuccessStatusCode();
    }
    private static async Task BothAsync(DeploymentScenario s){await CapableAsync(s,0);await CapableAsync(s,1);}

    [Fact]
    public async Task AllNineBindings_ManagementPipelineAndFrozenPolicyRevision()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await BothAsync(s);
        (string Type,string Config)[] configs=[
            ("authentication","{\"mode\":\"ApiKey\"}"),("timeout","{\"timeoutMs\":30000}"),
            ("rate_limit","{\"algorithm\":\"TokenBucket\",\"keyBy\":\"Route\",\"refillTokens\":100,\"windowMs\":1000,\"burst\":100,\"redisFailureMode\":\"Reject\"}"),
            ("circuit_breaker","{\"samplingWindowMs\":30000,\"minimumRequests\":20,\"failureRatio\":0.5,\"openDurationMs\":30000,\"halfOpenMaxRequests\":1,\"halfOpenSuccesses\":3,\"failureStatusCodes\":[503],\"countTimeouts\":true,\"countConnectionFailures\":true}"),
            ("retry","{\"maxAttempts\":3,\"perAttemptTimeoutMs\":1000,\"baseDelayMs\":10,\"maxDelayMs\":100,\"jitterPercent\":0,\"retryStatusCodes\":[503],\"retryConnectionFailures\":true}"),
            ("cache","{\"ttlSeconds\":60,\"maxEntryBytes\":1024,\"varyHeaders\":[],\"identityPartition\":\"VerifiedIdentity\",\"redisFailureMode\":\"Bypass\"}"),
            ("cors",Cors),("ip_filter","{\"mode\":\"AllowList\",\"ranges\":[\"192.0.2.0/24\"]}"),("transform",TransformPreviewTests.Config)];
        Guid routeId;var bindings=new List<WebApi.Contracts.Policies.PolicyBindingInput>();
        await using(var db=s.Api.Context()) {
            routeId=(await db.Set<ApiRoute>().SingleAsync()).Id;var role=await db.Set<UserRole>().Where(r=>r.UserId==s.Api.User.Id).Select(r=>r.RoleId).SingleAsync();
            foreach(var code in new[]{"policy.read","policy.write"}){var permission=new Permission{Code=code,Name=code,Module="policy"};db.Add(permission);db.Add(new RolePermission{RoleId=role,PermissionId=permission.Id});}
            foreach(var c in configs){var policy=new Policy{OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,Name=c.Type,Type=c.Type,Config=c.Config};db.Add(policy);bindings.Add(new(policy.Id,bindings.Count));}await db.SaveChangesAsync();
        }
        using var current=await s.Api.Client.GetAsync($"/api/v1/routes/{routeId}/policies");current.EnsureSuccessStatusCode();
        using var bound=await s.Api.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{routeId}/policies",new WebApi.Contracts.Policies.SaveRoutePoliciesRequest(bindings),current.Headers.ETag!.ToString());bound.EnsureSuccessStatusCode();
        Assert.Equal(9,(await bound.Content.ReadFromJsonAsync<WebApi.Contracts.Policies.RoutePoliciesDto>())!.Bindings.Count);
        var id=await s.ReadyAsync();byte[] frozen;
        await using(var db=s.Api.Context()){frozen=(await db.Set<ReleaseRecord>().SingleAsync()).CandidateBytes!.ToArray();var policy=await db.Set<Policy>().SingleAsync(p=>p.Type=="cors");policy.Config=Cors.Replace("portal.example.com","changed.example.com");policy.VersionNo++;await db.SaveChangesAsync();}
        await s.BuildAsync(id);var desired=await s.DesiredAsync(id);using var snapshot=JsonDocument.Parse(desired.Payload);Assert.Equal("2.3",snapshot.RootElement.GetProperty("schemaVersion").GetString());
        var config=snapshot.RootElement.GetProperty("policies").EnumerateArray().Single(p=>p.GetProperty("type").GetString()=="cors").GetProperty("config").GetString()!;Assert.Contains("portal.example.com",config);Assert.DoesNotContain("changed.example.com",config);
        await using(var db=s.Api.Context()){Assert.Equal(frozen,(await db.Set<ReleaseRecord>().SingleAsync()).CandidateBytes);Assert.False(db.Database.HasPendingModelChanges());}
        await s.AckAllAsync(id);
    }

    [Fact]
    public async Task FrozenBodyConfiguration_PublishRechecksTightenedDeploymentLimit()
    {
        var limit=1048576;await using var s=new DeploymentScenario();await s.InitializeAsync(b=>b.Configuration["GatewayPolicies:Transform:MaxBodyBytes"]=limit.ToString());await BothAsync(s);
        await using(var db=s.Api.Context()) {var route=await db.Set<ApiRoute>().SingleAsync();var p=new Policy{OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,Name="body",Type="transform",Config=TransformPreviewTests.Config.Replace("1024","2048")};db.Add(p);db.Add(new RoutePolicyBinding{RouteId=route.Id,PolicyId=p.Id});route.Revision++;await db.SaveChangesAsync();}
        var id=await s.ReadyAsync();limit=1024;await s.Api.RestartServerAsync();
        using var rejected=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/publish");Assert.Equal(HttpStatusCode.UnprocessableEntity,rejected.StatusCode);Assert.Contains("invalid_policy_config",await rejected.Content.ReadAsStringAsync());
        await using var check=s.Api.Context();Assert.Null((await check.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion);Assert.Equal("Ready",(await check.Set<ReleaseRecord>().SingleAsync()).Status);
    }

    [Fact]
    public async Task HistoricalRollback_PublishRechecksBodyLimitWithoutRewritingBytes()
    {
        var limit=1048576;await using var s=new DeploymentScenario();await s.InitializeAsync(b=>b.Configuration["GatewayPolicies:Transform:MaxBodyBytes"]=limit.ToString());await BothAsync(s);
        await using(var db=s.Api.Context()){var route=await db.Set<ApiRoute>().SingleAsync();var p=new Policy{OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,Name="body",Type="transform",Config=TransformPreviewTests.Config.Replace("1024","2048")};db.Add(p);db.Add(new RoutePolicyBinding{RouteId=route.Id,PolicyId=p.Id});route.Revision++;await db.SaveChangesAsync();}
        var initial=await s.PublishAsync();var original=await s.DesiredAsync(initial);
        await using(var db=s.Api.Context())await db.Set<RoutePolicyBinding>().ExecuteDeleteAsync();var latest=await s.PublishAsync();
        limit=1024;await s.Api.RestartServerAsync();
        using var created=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{latest}/rollback",new {targetConfigVersion=1L});created.EnsureSuccessStatusCode();var id=(await created.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
        using var submitted=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/submit");submitted.EnsureSuccessStatusCode();await s.ApproveAsync(id);
        using var rejected=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/publish");Assert.Equal(HttpStatusCode.UnprocessableEntity,rejected.StatusCode);
        var preserved=await s.DesiredAsync(initial);Assert.Equal(original.Payload,preserved.Payload);Assert.Equal(original.Envelope.PayloadHash,preserved.Envelope.PayloadHash);
        await using var check=s.Api.Context();Assert.Equal(2,(await check.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion);
    }

    [Fact]
    public async Task CandidateCreation_OldNodeBlocks23WithoutShrinkingCohort()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await BindAsync(s);
        using var create=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/environments/{s.Api.Environment.Id}/releases",new CreateReleaseRequest(0,[s.Api.Version.Id],[]));
        Assert.Equal(HttpStatusCode.Conflict,create.StatusCode);Assert.Contains("gateway_schema_unsupported",await create.Content.ReadAsStringAsync());
        await using var db=s.Api.Context();Assert.False(await db.Set<ReleaseRecord>().AnyAsync());Assert.Equal(2,await db.Set<GatewayNode>().CountAsync(n=>n.Enabled));Assert.Null((await db.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion);
    }

    [Fact]
    public async Task PublishWorker_RechecksActual23AndLeavesOriginalDesiredState()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await BothAsync(s);await BindAsync(s);var id=await s.ReadyAsync();
        using var accepted=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/publish");accepted.EnsureSuccessStatusCode();
        await CapableAsync(s,1,network:false);await s.BuildQueuedAsync();
        await using var db=s.Api.Context();var release=await db.Set<ReleaseRecord>().SingleAsync();Assert.Equal("Failed",release.Status);Assert.Equal("gateway_schema_unsupported",release.FailureCode);
        var env=await db.Set<EnvironmentRecord>().SingleAsync();Assert.Null(env.DesiredConfigVersion);Assert.Equal(0,env.DeploymentSequence);Assert.False(await db.Set<GatewayConfigVersion>().AnyAsync());
    }

    [Fact]
    public async Task CurrentInstanceOnly_MayAck23()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await BothAsync(s);await BindAsync(s);var id=await s.PublishAsync(false);var desired=await s.DesiredAsync(id);
        using var document=JsonDocument.Parse(desired.Payload);Assert.Equal("2.3",document.RootElement.GetProperty("schemaVersion").GetString());
        var replacement=Guid.NewGuid();await CapableAsync(s,0,replacement);
        using var stale=await s.NodeAsync(0,$"/internal/v1/nodes/{s.NodeIds[0]}/ack",new NodeAck(s.Instances[0],id,desired.Envelope.ConfigVersion,desired.Envelope.DeploymentSequence,desired.Envelope.PayloadHash,DateTimeOffset.UtcNow,true,null));Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        using var premature=await s.NodeAsync(0,$"/internal/v1/nodes/{s.NodeIds[0]}/ack",new NodeAck(replacement,id,desired.Envelope.ConfigVersion,desired.Envelope.DeploymentSequence,desired.Envelope.PayloadHash,DateTimeOffset.UtcNow,true,null));Assert.Equal(HttpStatusCode.Conflict,premature.StatusCode);
        await using(var db=s.Api.Context())Assert.False(await db.Set<GatewayAck>().AnyAsync());
        await CapableAsync(s,0);await s.AckAllAsync(id);
        await CapableAsync(s,0,replacement);using var runtime=await s.NodeAsync(0,$"/internal/v1/nodes/{s.NodeIds[0]}/ack",new NodeAck(replacement,id,desired.Envelope.ConfigVersion,desired.Envelope.DeploymentSequence,desired.Envelope.PayloadHash,DateTimeOffset.UtcNow,true,null));runtime.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task UnsupportedInstance_CannotAck23OrRetryItsArtifact()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await BothAsync(s);await BindAsync(s);var id=await s.PublishAsync(false);var desired=await s.DesiredAsync(id);
        await CapableAsync(s,0,network:false);
        using var ack=await s.NodeAsync(0,$"/internal/v1/nodes/{s.NodeIds[0]}/ack",new NodeAck(s.Instances[0],id,desired.Envelope.ConfigVersion,desired.Envelope.DeploymentSequence,desired.Envelope.PayloadHash,DateTimeOffset.UtcNow,true,null));Assert.Equal(HttpStatusCode.Conflict,ack.StatusCode);Assert.Contains("gateway_schema_unsupported",await ack.Content.ReadAsStringAsync());
        await using(var db=s.Api.Context()){Assert.False(await db.Set<GatewayAck>().AnyAsync());(await db.Set<ReleaseRecord>().SingleAsync()).Status="Failed";await db.SaveChangesAsync();}
        using var retry=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/retry");Assert.Equal(HttpStatusCode.Conflict,retry.StatusCode);Assert.Contains("gateway_schema_unsupported",await retry.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RollbackCreation_UsesActualHistorical23RatherThanCurrentLegacy()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await BothAsync(s);await BindAsync(s);await s.PublishAsync();
        await using(var db=s.Api.Context())await db.Set<RoutePolicyBinding>().ExecuteDeleteAsync();var latest=await s.PublishAsync();await CapableAsync(s,1,network:false);
        using var rollback=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{latest}/rollback",new {targetConfigVersion=1L});Assert.Equal(HttpStatusCode.Conflict,rollback.StatusCode);Assert.Contains("gateway_schema_unsupported",await rollback.Content.ReadAsStringAsync());
        await using var check=s.Api.Context();Assert.Equal(2,(await check.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion);Assert.Equal(2,await check.Set<ReleaseRecord>().CountAsync());
    }
}

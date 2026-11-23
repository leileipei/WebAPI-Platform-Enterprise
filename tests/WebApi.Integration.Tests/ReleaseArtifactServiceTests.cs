using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Releases;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ReleaseArtifactServiceTests
{
    private static async Task<(DeploymentScenario Scenario,Guid Release)> Source(bool acknowledge=true)
    {
        var s=new DeploymentScenario();await s.InitializeAsync();await using(var db=s.Api.Context()){
            var v=await db.Set<ApiVersion>().SingleAsync();v.OpenapiDocument="{\"openapi\":\"3.0.3\",\"servers\":[{\"url\":\"https://test-gateway.example\"}]}";v.OpenapiSource="source-document-test-upstream";
            var e=await db.Set<EnvironmentRecord>().SingleAsync();e.GatewayPublicUrl="https://test-gateway.example";e.GatewayInternalUrl="http://test-private.example";e.BasePath="/test-prefix";await db.SaveChangesAsync();
        }
        var release=await s.PublishAsync(acknowledge);await using(var db=s.Api.Context()){(await db.Set<EnvironmentRecord>().SingleAsync()).IsProduction=false;await db.SaveChangesAsync();}return(s,release);
    }
    private static Task<HttpResponseMessage> Create(DeploymentScenario s,Guid release)=>ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{release}/artifacts");
    private static async Task<Guid> DeployAsync(DeploymentScenario scenario, string path, object request)
    {
        using var created = await ApiFixture.CommandAsync(scenario.Api.Client, path, request);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
        using var submitted = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{id}/submit");
        submitted.EnsureSuccessStatusCode();
        await scenario.BuildAsync(id);
        await scenario.AckAllAsync(id);
        return id;
    }

    [Theory]
    [InlineData(1, "/orders")]
    [InlineData(2, "/returns")]
    public async Task ExactRollbackRebuildsAndVerifiesOriginalPublishCandidate(long targetVersion, string expectedPath)
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var api = scenario.Api;
        await using (var db = api.Context())
            await db.Set<EnvironmentRecord>().Where(env => env.Id == api.Environment.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(env => env.IsProduction, false));
        var first = await scenario.PublishAsync();

        var returns = new Api { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id,
            Code = "RETURNS", Name = "Returns", OwnerUserId = api.User.Id };
        var returnsVersion = new ApiVersion { ApiId = returns.Id, Version = "1.0", CreatedBy = api.User.Id };
        await using (var db = api.Context())
        {
            db.AddRange(returns, returnsVersion);
            db.Add(new ApiRoute { ApiId = returns.Id, ApiVersionId = returnsVersion.Id, EnvironmentId = api.Environment.Id,
                ClusterId = api.Cluster.Id, RouteName = "Returns", Path = "/returns", NormalizedPath = "/returns",
                Methods = ["GET"], TimeoutMs = 30000 });
            await db.SaveChangesAsync();
        }
        var second = await DeployAsync(scenario, $"/api/v1/environments/{api.Environment.Id}/releases",
            await api.PreviewReleaseRequestAsync(1, returnsVersion.Id));
        var secondSnapshot = SnapshotValidator.ParsePayload((await scenario.DesiredAsync(second)).Payload, api.Environment.Id);
        Assert.Equal(new[] { "/orders", "/returns" }, secondSnapshot.Routes.Select(route => route.Path).Order());

        var origin = targetVersion == 1 ? first : second;
        var current = targetVersion == 1 ? second : await scenario.PublishAsync();
        var expected = await scenario.DesiredAsync(origin);
        var rollback = await DeployAsync(scenario, $"/api/v1/releases/{current}/rollback", new CreateRollbackRequest(targetVersion));
        using var result = await Create(scenario, rollback);
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
        var artifact = (await result.Content.ReadFromJsonAsync<ReleaseArtifactDto>())!;
        Assert.Equal(rollback, artifact.SourceReleaseId);
        Assert.Equal(expected.Envelope.PayloadHash, artifact.SourceSnapshotHash);
        Assert.Equal(targetVersion == 1 ? api.Api.Id : returns.Id, Assert.Single(artifact.Content.Apis).ApiId);
        Assert.Equal(expectedPath, Assert.Single(artifact.Content.Routes).Path);

        await using (var db = api.Context())
        {
            var published = await db.Set<ReleaseRecord>().SingleAsync(row => row.Id == origin);
            var frozen = JsonSerializer.Deserialize<FrozenReleaseCandidate>(published.CandidateBytes!, CanonicalJson.Options)!;
            var route = Assert.Single(frozen.Routes) with { Path = "/altered", NormalizedPath = "/altered" };
            published.CandidateBytes = CanonicalJson.Serialize(frozen with { Routes = [route] });
            await db.SaveChangesAsync();
        }
        using var corrupt = await Create(scenario, rollback);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, corrupt.StatusCode);
        Assert.Equal("corrupt_release_artifact_source", (await corrupt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AcknowledgedRetryCreatesArtifactForTheRecoveredPublish()
    {
        var (scenario, failed) = await Source(acknowledge: false);
        await using var lifetime = scenario;
        var original = await scenario.DesiredAsync(failed);
        var target = original.Envelope;
        using var failedAck = await scenario.NodeAsync(0, $"/internal/v1/nodes/{scenario.NodeIds[0]}/ack",
            new NodeAck(scenario.Instances[0], failed, target.ConfigVersion, target.DeploymentSequence,
                target.PayloadHash, DateTimeOffset.UtcNow, false, "activation_timeout"));
        failedAck.EnsureSuccessStatusCode();
        using var retry = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{failed}/retry");
        retry.EnsureSuccessStatusCode();
        var id = (await retry.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
        await scenario.BuildQueuedAsync();
        await scenario.AckAllAsync(id);
        using var created = await Create(scenario, id);
        created.EnsureSuccessStatusCode();
        var artifact = (await created.Content.ReadFromJsonAsync<ReleaseArtifactDto>())!;
        Assert.Equal(id, artifact.SourceReleaseId);
        Assert.Equal(original.Envelope.PayloadHash, artifact.SourceSnapshotHash);
        Assert.Equal("/orders", Assert.Single(artifact.Content.Routes).Path);
    }

    [Fact] public async Task SuccessfulSourceCreatesPortableArtifactAndRepeatedCreationReturnsSameId()
    {
        var (s,id)=await Source();await using var lifetime=s;using var response=await Create(s,id);response.EnsureSuccessStatusCode();var a=(await response.Content.ReadFromJsonAsync<ReleaseArtifactDto>())!;using var duplicate=await Create(s,id);duplicate.EnsureSuccessStatusCode();Assert.Equal(a.Id,(await duplicate.Content.ReadFromJsonAsync<ReleaseArtifactDto>())!.Id);Assert.NotEqual(a.ArtifactHash,a.SourceSnapshotHash);Assert.Equal(64,a.ArtifactHash.Length);Assert.Equal(id,a.SourceReleaseId);Assert.Single(a.Content.Routes);Assert.Equal("ApiKey",a.Content.Routes[0].AuthenticationMode);
        var text=Encoding.UTF8.GetString(WebApi.Contracts.Common.CanonicalJson.Serialize(a.Content));foreach(var forbidden in new[]{"test-gateway.example","test-private.example","test-backend:8080","test-prefix","openapiSource","openapiDocument","source-document-test-upstream"})Assert.DoesNotContain(forbidden,text);
        await using var db=s.Api.Context();foreach(var credential in await db.Set<ApplicationCredential>().ToArrayAsync()){Assert.DoesNotContain(credential.AccessKey,text);Assert.DoesNotContain(credential.SecretHash,text);}Assert.Equal("Succeeded",(await db.Set<ReleaseRecord>().SingleAsync()).Status);
    }
    [Fact] public async Task PartialAcknowledgementDoesNotQualifyAsSuccessfulSource()
    {
        var (s,id)=await Source(false);await using var lifetime=s;using var response=await Create(s,id);Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
    }
    [Theory] [InlineData("payload")] [InlineData("hash")] [InlineData("candidate")]
    public async Task CorruptSnapshotOrFrozenCandidateCannotProduceArtifact(string corrupt)
    {
        var (s,id)=await Source();await using var lifetime=s;await using(var db=s.Api.Context()){
            if(corrupt=="payload")(await db.Set<GatewayConfigSnapshot>().SingleAsync()).PayloadBytes=Encoding.UTF8.GetBytes("{}");
            if(corrupt=="hash")(await db.Set<GatewayConfigVersion>().SingleAsync()).SnapshotHash=new string('a',64);
            if(corrupt=="candidate"){var r=await db.Set<ReleaseRecord>().SingleAsync();var c=JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes!,WebApi.Contracts.Common.CanonicalJson.Options)!;r.CandidateBytes=WebApi.Contracts.Common.CanonicalJson.Serialize(c with{Routes=[c.Routes[0] with{Path="/altered",NormalizedPath="/altered"}]});}
            await db.SaveChangesAsync();
        }
        using var response=await Create(s,id);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
    }
    [Theory] [InlineData("node_hash")] [InlineData("node_offline")] [InlineData("sequence")]
    public async Task CurrentRuntimeMustMatchActualSourceDeployment(string changed)
    {
        var (s,id)=await Source();await using var lifetime=s;await using(var db=s.Api.Context()){
            if(changed=="node_hash"){await db.Set<GatewayAck>().Where(x=>x.NodeId==s.NodeIds[0]).ExecuteUpdateAsync(p=>p.SetProperty(x=>x.PayloadHash,new string('f',64)));}
            if(changed=="node_offline")await db.Set<GatewayNode>().Where(x=>x.Id==s.NodeIds[0]).ExecuteUpdateAsync(p=>p.SetProperty(x=>x.LastHeartbeatAt,DateTimeOffset.UtcNow.AddHours(-1)));
            if(changed=="sequence")await db.Set<EnvironmentRecord>().ExecuteUpdateAsync(p=>p.SetProperty(x=>x.DeploymentSequence,99));
            await db.SaveChangesAsync();
        }
        using var response=await Create(s,id);Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
    }
    [Fact] public async Task ArtifactReadRechecksContractVisibilityAndScope()
    {
        var (s,id)=await Source();await using var lifetime=s;using var created=await Create(s,id);created.EnsureSuccessStatusCode();var artifact=(await created.Content.ReadFromJsonAsync<ReleaseArtifactDto>())!;
        await using(var db=s.Api.Context()){var permission=await db.Set<Permission>().SingleAsync(x=>x.Code=="api.schema.read");await db.Set<RolePermission>().Where(x=>x.PermissionId==permission.Id).ExecuteDeleteAsync();}
        using var hidden=await s.Api.Client.GetAsync($"/api/v1/release-artifacts/{artifact.Id}");Assert.Equal(HttpStatusCode.NotFound,hidden.StatusCode);Assert.DoesNotContain("canonicalContent",await hidden.Content.ReadAsStringAsync());
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task SelectedVersionLabelMustMatchTheSealedVersion(bool frozen)
    {
        var (s,id)=await Source();await using var lifetime=s;await using(var db=s.Api.Context()){
            if(frozen){var r=await db.Set<ReleaseRecord>().SingleAsync();var c=JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes!,WebApi.Contracts.Common.CanonicalJson.Options)!;r.CandidateBytes=WebApi.Contracts.Common.CanonicalJson.Serialize(c with{Versions=[c.Versions[0] with{Version=c.Versions[0].Version with{Version="forged-label"}}]});}
            else (await db.Set<ApiVersion>().SingleAsync()).Version="forged-label";
            await db.SaveChangesAsync();
        }
        using var response=await Create(s,id);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
    }
}

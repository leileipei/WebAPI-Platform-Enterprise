using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
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

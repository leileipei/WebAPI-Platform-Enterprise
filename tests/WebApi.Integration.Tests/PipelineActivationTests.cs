using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PipelineActivationTests
{
    internal static async Task<Guid> VersionAsync(PipelineScenario s)
    {var draft=await PipelineDefinitionTests.DraftAsync(s);return (await PipelineDefinitionTests.PublishAsync(s,draft.Id)).GetProperty("id").GetGuid();}
    internal static async Task<HttpResponseMessage> PolicyCommandAsync(PipelineScenario s,string action,object body,string? key=null)
    {
        using var current=await s.Creator.GetAsync($"/api/v1/projects/{s.Api.Project.Id}/delivery-policy");current.EnsureSuccessStatusCode();
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/projects/{s.Api.Project.Id}/delivery-policy/{action}"){Content=JsonContent.Create(body)};
        request.Headers.Add("X-CSRF-Token",await s.Api.CsrfAsync());request.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString("N"));request.Headers.Add("If-Match",current.Headers.ETag!.Tag);return await s.Creator.SendAsync(request);
    }
    internal static Task<HttpResponseMessage> ActivateAsync(PipelineScenario s,Guid id,string? key=null)=>PolicyCommandAsync(s,"activate-pipeline",new ActivatePipelineRequest(id),key);
    internal static async Task SeedBaselineAsync(PipelineScenario s)
    {
        // Persistence-level synthetic fixture, never a real Gateway proof.
        await using var db=s.Api.Context();var environment=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==s.Environments[s.Environments.Length-1]);
        var payload=CanonicalJson.Serialize(new RuntimeSnapshot("2.0",environment.Id,1,DateTimeOffset.UtcNow,[],[],[],[]));var hash=Convert.ToHexStringLower(SHA256.HashData(payload));
        var config=new GatewayConfigVersion{EnvironmentId=environment.Id,VersionNo=1,SnapshotHash=hash,CreatedBy=s.Api.User.Id};db.Add(config);db.Add(new GatewayConfigSnapshot{ConfigVersionId=config.Id,Payload=Encoding.UTF8.GetString(payload),PayloadBytes=payload,SizeBytes=payload.LongLength});
        var release=new ReleaseRecord{EnvironmentId=environment.Id,ReleaseNo="pipeline-baseline-fixture",Status="Succeeded",ReleaseType="publish",RequestedBy=s.Api.User.Id,ToConfigVersion=1,DeploymentSequence=1,CompletedAt=DateTimeOffset.UtcNow};db.Add(release);environment.DesiredConfigVersion=1;environment.DeploymentSequence=1;
        for(var i=0;i<2;i++){var node=new GatewayNode{EnvironmentId=environment.Id,NodeName="pipeline-target-"+i,InstanceId=Guid.NewGuid().ToString(),IdentityHash=new string('f',64),LastHeartbeatAt=DateTimeOffset.UtcNow,CurrentConfigVersion=1,CurrentDeploymentSequence=1};db.Add(node);db.Add(new ReleaseTarget{ReleaseId=release.Id,NodeId=node.Id,InstanceId=node.InstanceId});db.Add(new GatewayAck{ReleaseId=release.Id,NodeId=node.Id,InstanceId=node.InstanceId,ConfigVersion=1,DeploymentSequence=1,PayloadHash=hash,Success=true});}
        await db.SaveChangesAsync();
    }
    [Theory][InlineData("entry")][InlineData("flow")][InlineData("baseline")]
    public async Task ActivateRequiresProductionEntryFlowAndRealBaseline(string missing)
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var id=await VersionAsync(s);if(missing!="baseline")await SeedBaselineAsync(s);
        await using(var db=s.Api.Context()){if(missing=="entry")(await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==s.Environments[s.Environments.Length-1])).GatewayPublicUrl=null;if(missing=="flow")(await db.Set<ApprovalFlow>().SingleAsync()).Enabled=false;await db.SaveChangesAsync();}
        using var response=await ActivateAsync(s,id);Assert.Contains(response.StatusCode,new[]{HttpStatusCode.Conflict,HttpStatusCode.UnprocessableEntity});
    }
    [Fact] public async Task ExplicitActivationSetsLastAdjacentSummaryAndBlocksLegacyPut()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);var id=await VersionAsync(s);await SeedBaselineAsync(s);using var response=await ActivateAsync(s,id);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var policy=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("PipelineRequired",policy.GetProperty("mode").GetString());Assert.Equal(s.Environments[^2],policy.GetProperty("sourceEnvironmentId").GetGuid());Assert.Equal(id,policy.GetProperty("activePipelineVersionId").GetGuid());
        using var put=await s.Api.WriteAsync(HttpMethod.Put,$"/api/v1/projects/{s.Api.Project.Id}/delivery-policy",new SaveDeliveryPolicyRequest(s.Environments[0],s.Environments[s.Environments.Length-1],"Legacy",["InterfaceFunction"],1440),"\"1\"");Assert.Equal(HttpStatusCode.Conflict,put.StatusCode);
    }
    [Fact] public async Task LegacyPutCannotDisablePipelineMode()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var id=await VersionAsync(s);await SeedBaselineAsync(s);
        using var active=await ActivateAsync(s,id);Assert.Equal(HttpStatusCode.OK,active.StatusCode);
        using var put=await s.Api.WriteAsync(HttpMethod.Put,$"/api/v1/projects/{s.Api.Project.Id}/delivery-policy",new SaveDeliveryPolicyRequest(s.Environments[0],s.Environments[s.Environments.Length-1],"Legacy",["InterfaceFunction"],1440),"\"1\"");Assert.Equal(HttpStatusCode.Conflict,put.StatusCode);
    }
    [Fact] public async Task ActivationRequiresEveryTargetWriteScope()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync(4);var id=await VersionAsync(s);await SeedBaselineAsync(s);
        await using(var db=s.Api.Context()){
            await db.Set<UserProjectScope>().Where(x=>x.UserId==s.Api.User.Id).ExecuteDeleteAsync();
            db.Add(new UserProjectScope{UserId=s.Api.User.Id,OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,AccessMode="read_write"});
            var permission=await db.Set<Permission>().SingleAsync(p=>p.Code=="environment.write");await db.Set<RolePermission>().Where(r=>r.PermissionId==permission.Id).ExecuteDeleteAsync();await db.SaveChangesAsync();}
        using var denied=await ActivateAsync(s,id);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }
    [Fact] public async Task ActivePipelineCannotArchiveUntilExplicitlyRestored()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var draft=await PipelineDefinitionTests.DraftAsync(s);var v=await PipelineDefinitionTests.PublishAsync(s,draft.Id);await SeedBaselineAsync(s);using var active=await ActivateAsync(s,v.GetProperty("id").GetGuid());Assert.Equal(HttpStatusCode.OK,active.StatusCode);
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/release-pipelines/{draft.Id}/archive"){Content=JsonContent.Create(new{})};request.Headers.Add("X-CSRF-Token",await s.Api.CsrfAsync());request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));request.Headers.Add("If-Match","\"2\"");using var denied=await s.Creator.SendAsync(request);Assert.Equal(HttpStatusCode.Conflict,denied.StatusCode);
    }
    [Theory][InlineData("Paused")][InlineData("TimedOut")]
    public async Task PausedAndTimedOutRunBlockModeSwitch(string status)
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var id=await VersionAsync(s);await SeedBaselineAsync(s);using var activated=await ActivateAsync(s,id);Assert.Equal(HttpStatusCode.OK,activated.StatusCode);
        await using(var db=s.Api.Context()){db.Add(new ReleasePipelineRun{OrganizationId=s.Api.Organization.Id,ProjectId=s.Api.Project.Id,PipelineVersionId=id,DefinitionHash=new string('a',64),RootArtifactId=s.RootArtifactId,RootArtifactHash=s.Source.Artifact.ArtifactHash,SourceEnvironmentId=s.Environments[0],SourceReleaseId=s.Source.ReleaseId,SourceConfigVersion=1,SourceDeploymentSequence=1,PolicyRevision=1,Status=status,CreatedBy=s.Api.User.Id});await db.SaveChangesAsync();}
        using var restore=await PolicyCommandAsync(s,"restore-connection",new RestoreDeliveryPolicyRequest(new(s.Environments[0],s.Environments[s.Environments.Length-1],"Legacy",["InterfaceFunction"],1440)));Assert.Equal(HttpStatusCode.Conflict,restore.StatusCode);
        using var reactivate=await ActivateAsync(s,id);Assert.Equal(HttpStatusCode.Conflict,reactivate.StatusCode);
    }
    [Fact] public async Task ConcurrentActivationProducesOneRuleRevisionAndSameKeyReplays()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var id=await VersionAsync(s);await SeedBaselineAsync(s);var key=Guid.NewGuid().ToString("N");
        using var one=await ActivateAsync(s,id,key);Assert.Equal(HttpStatusCode.OK,one.StatusCode);var text=await one.Content.ReadAsStringAsync();using var replayRequest=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/projects/{s.Api.Project.Id}/delivery-policy/activate-pipeline"){Content=JsonContent.Create(new ActivatePipelineRequest(id))};replayRequest.Headers.Add("X-CSRF-Token",await s.Api.CsrfAsync());replayRequest.Headers.Add("Idempotency-Key",key);replayRequest.Headers.Add("If-Match","\"0\"");using var replay=await s.Creator.SendAsync(replayRequest);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(text,await replay.Content.ReadAsStringAsync());
        var responses=await Task.WhenAll(ActivateAsync(s,id),ActivateAsync(s,id));foreach(var response in responses){Assert.Equal(HttpStatusCode.OK,response.StatusCode);response.Dispose();}
        await using var db=s.Api.Context();Assert.Equal(1,(await db.Set<ProjectDeliveryPolicy>().SingleAsync()).Revision);
    }
    [Fact] public async Task ActivationRejectsChangedFrozenTemplate()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var id=await VersionAsync(s);await SeedBaselineAsync(s);await using(var db=s.Api.Context()){var flow=await db.Set<ApprovalFlow>().SingleAsync();flow.Revision++;await db.SaveChangesAsync();}
        using var stale=await ActivateAsync(s,id);Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
    }
    [Fact] public async Task RestoreIsExplicitAndCannotAcceptPipelineMode()
    {
        await using var s=new PipelineScenario();await s.InitializeEnvironmentsAsync();var id=await VersionAsync(s);await SeedBaselineAsync(s);using var active=await ActivateAsync(s,id);Assert.Equal(HttpStatusCode.OK,active.StatusCode);
        var connection=new SaveDeliveryPolicyRequest(s.Environments[0],s.Environments[s.Environments.Length-1],"Legacy",["InterfaceFunction"],1440);
        using var restore=await PolicyCommandAsync(s,"restore-connection",new RestoreDeliveryPolicyRequest(connection));Assert.Equal(HttpStatusCode.OK,restore.StatusCode);Assert.Equal("Legacy",(await restore.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mode").GetString());
    }
}

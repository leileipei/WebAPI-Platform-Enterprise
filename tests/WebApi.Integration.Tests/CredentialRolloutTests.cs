using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class CredentialRolloutTests
{
    private sealed class Fixture:IAsyncDisposable
    {
        public readonly ApiFixture Api=new();public Guid Old=Guid.NewGuid(),New=Guid.NewGuid(),Release=Guid.NewGuid();public string Hash="";public long Version;
        public async Task InitializeAsync(bool revoked=false,bool missingNew=false)
        {
            await Api.InitializeAsync();await Api.SeedReleaseAsync();using var login=await Api.LoginAsync();login.EnsureSuccessStatusCode();var now=DateTimeOffset.UtcNow;Version=revoked?2:1;var material=ApiKeySecret.Create();
            var keys=new List<RuntimeCredential>();if(!revoked)keys.Add(new(Old,"old-material",material.Hash,"Active",now.AddDays(-1),now.AddDays(1)));if(!missingNew)keys.Add(new(New,"new-material",material.Hash,"Active",now.AddDays(-1),now.AddDays(1)));
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new RuntimeSnapshot("2.0",Api.Environment.Id,Version,now,[],[],[],[new RuntimeApplication(Api.Application.Id,"Active",keys,[])]),CanonicalJson.Options);Hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
            await using var db=Api.Context();var version=new GatewayConfigVersion{EnvironmentId=Api.Environment.Id,VersionNo=Version,CreatedBy=Api.User.Id,SnapshotHash=Hash};db.Add(version);db.Add(new GatewayConfigSnapshot{ConfigVersionId=version.Id,PayloadBytes=bytes,SizeBytes=bytes.Length});db.Add(new ReleaseRecord{Id=Release,EnvironmentId=Api.Environment.Id,ReleaseNo="rollout",ReleaseType="publish",Status="Succeeded",RequestedBy=Api.User.Id,ToConfigVersion=Version,DeploymentSequence=Version});
            var environment=await db.Set<EnvironmentRecord>().SingleAsync();environment.DesiredConfigVersion=Version;environment.DeploymentSequence=Version;
            for(var i=0;i<2;i++){var node=new GatewayNode{EnvironmentId=Api.Environment.Id,NodeName="rollout-"+i,InstanceId=Guid.NewGuid().ToString(),CurrentConfigVersion=Version,CurrentDeploymentSequence=Version,LastHeartbeatAt=now,Enabled=true};db.Add(node);db.Add(new ReleaseTarget{ReleaseId=Release,NodeId=node.Id,InstanceId=node.InstanceId});db.Add(new GatewayAck{ReleaseId=Release,NodeId=node.Id,InstanceId=node.InstanceId,ConfigVersion=Version,DeploymentSequence=Version,PayloadHash=Hash,Success=true});}
            await db.SaveChangesAsync();
        }
        public string Path(string stage="Overlap")=>$"/api/v1/applications/{Api.Application.Id}/credential-rollout?environmentId={Api.Environment.Id}&releaseId={Release}&newCredentialId={New}&oldCredentialId={Old}&stage={stage}";
        public ValueTask DisposeAsync()=>Api.DisposeAsync();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Rollout_returns_only_fresh_current_snapshot_hash_and_two_matching_nodes(bool revoked)
    {
        await using var f=new Fixture();await f.InitializeAsync(revoked);using var response=await f.Api.Client.GetAsync(f.Path(revoked?"Revoked":"Overlap"));response.EnsureSuccessStatusCode();var json=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(f.Hash,json.GetProperty("payloadHash").GetString());Assert.Equal(f.Version.ToString(),json.GetProperty("configVersion").GetString());Assert.Equal(2,json.GetProperty("nodeIds").GetArrayLength());Assert.DoesNotContain("material",json.GetRawText());
    }
    [Theory][InlineData("hash")][InlineData("instance")][InlineData("stale")][InlineData("newer")][InlineData("only-one")]
    public async Task Historical_or_mismatched_ack_cannot_advance_rotation(string kind)
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Api.Context())
        {
            var node=await db.Set<GatewayNode>().FirstAsync();
            if(kind=="hash")await db.Set<GatewayAck>().Where(x=>x.NodeId==node.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.PayloadHash,new string('f',64)));
            if(kind=="instance")node.InstanceId=Guid.NewGuid().ToString();
            if(kind=="stale")node.LastHeartbeatAt=DateTimeOffset.UtcNow.AddMinutes(-5);
            if(kind=="newer")await db.Set<EnvironmentRecord>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.DeploymentSequence,99));
            if(kind=="only-one")node.Enabled=false;
            await db.SaveChangesAsync();
        }
        using var response=await f.Api.Client.GetAsync(f.Path());Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task New_credential_must_be_published_and_second_stage_must_remove_old_eligibility(bool missingNew)
    {
        await using var f=new Fixture();await f.InitializeAsync(missingNew:missingNew);using var response=await f.Api.Client.GetAsync(f.Path(missingNew?"Overlap":"Revoked"));Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
    }
    [Fact]
    public async Task App_read_does_not_replace_environment_release_read()
    {
        await using var f=new Fixture();await f.InitializeAsync();await using(var db=f.Api.Context()){var permission=await db.Set<Permission>().Where(x=>x.Code=="release.read").Select(x=>x.Id).SingleAsync();await db.Set<RolePermission>().Where(x=>x.PermissionId==permission).ExecuteDeleteAsync();}
        using var response=await f.Api.Client.GetAsync(f.Path());Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
    }
    [Fact]
    public async Task Revoked_working_key_can_restore_first_historical_proof_without_claiming_it_current()
    {
        await using var f=new Fixture();await f.InitializeAsync(revoked:true);var first=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        await using(var db=f.Api.Context())
        {
            db.Add(new ApplicationCredential{Id=f.Old,ApplicationId=f.Api.Application.Id,AccessKey="retired-working",SecretHash=new string('a',64),SecretLast4="1234",Status="Revoked",ValidFrom=now.AddDays(-1),ExpiresAt=now.AddDays(1)});
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new RuntimeSnapshot("2.0",f.Api.Environment.Id,1,now.AddMinutes(-10),[],[],[],[new RuntimeApplication(f.Api.Application.Id,"Active",[new(f.Old,"historical-old",new string('a',64),"Active",now.AddDays(-1),now.AddDays(1)),new(f.New,"historical-new",new string('b',64),"Active",now.AddDays(-1),now.AddDays(1))],[])]),CanonicalJson.Options);var hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
            var version=new GatewayConfigVersion{EnvironmentId=f.Api.Environment.Id,VersionNo=1,CreatedBy=f.Api.User.Id,SnapshotHash=hash};db.Add(version);db.Add(new GatewayConfigSnapshot{ConfigVersionId=version.Id,PayloadBytes=bytes,SizeBytes=bytes.Length});db.Add(new ReleaseRecord{Id=first,EnvironmentId=f.Api.Environment.Id,ReleaseNo="first-overlap",ReleaseType="publish",Status="Succeeded",RequestedBy=f.Api.User.Id,ToConfigVersion=1,DeploymentSequence=1});
            foreach(var node in await db.Set<GatewayNode>().ToArrayAsync()){db.Add(new ReleaseTarget{ReleaseId=first,NodeId=node.Id,InstanceId=node.InstanceId});db.Add(new GatewayAck{ReleaseId=first,NodeId=node.Id,InstanceId=node.InstanceId,ConfigVersion=1,DeploymentSequence=1,PayloadHash=hash,Success=true,AppliedAt=now.AddMinutes(-9)});}await db.SaveChangesAsync();
        }
        using var historical=await f.Api.Client.GetAsync(f.Path().Replace(f.Release.ToString(),first.ToString(),StringComparison.Ordinal));historical.EnsureSuccessStatusCode();var proof=await historical.Content.ReadFromJsonAsync<JsonElement>();Assert.False(proof.GetProperty("current").GetBoolean());Assert.Equal("1",proof.GetProperty("configVersion").GetString());
        using var current=await f.Api.Client.GetAsync(f.Path("Revoked"));current.EnsureSuccessStatusCode();Assert.True((await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("current").GetBoolean());
    }

}

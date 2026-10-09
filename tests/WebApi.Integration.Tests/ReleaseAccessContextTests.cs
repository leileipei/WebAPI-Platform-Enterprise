using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ReleaseAccessContextTests
{
    private static async Task SetOrigin(ApiFixture api,string origin,string prefix="/gateway")
    {
        using var response=await api.Client.GetAsync($"/api/v1/environments/{api.Environment.Id}");response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var e=json.RootElement;
        using var save=await api.WriteAsync(HttpMethod.Put,$"/api/v1/environments/{api.Environment.Id}",new {code=e.GetProperty("code").GetString(),name=e.GetProperty("name").GetString(),status="Active",isProduction=e.GetProperty("isProduction").GetBoolean(),releasePolicyId=e.GetProperty("releasePolicyId").GetGuid(),sortOrder=0,gatewayPublicUrl=origin,gatewayInternalUrl="http://private.test",basePath=prefix},response.Headers.ETag!.ToString());save.EnsureSuccessStatusCode();
    }
    private static async Task<JsonDocument> Release(ApiFixture api,Guid id)
    {using var response=await api.Client.GetAsync($"/api/v1/releases/{id}");response.EnsureSuccessStatusCode();return JsonDocument.Parse(await response.Content.ReadAsStringAsync());}
    private static async Task<long> Count(ApiFixture api)
    {await using var c=await api.Database.OpenAsync();await using var cmd=new NpgsqlCommand("select count(*) from release_access_contexts",c);return (long)(await cmd.ExecuteScalarAsync())!;}
    [Fact] public async Task DuplicatePublishKeepsFirstContext()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await SetOrigin(s.Api,"https://before.test");var id=await s.ReadyAsync();var key=Guid.NewGuid().ToString("N");
        using var first=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/publish",key:key);first.EnsureSuccessStatusCode();using var original=JsonDocument.Parse(await first.Content.ReadAsStringAsync());Assert.Equal("https://before.test",original.RootElement.GetProperty("accessContext").GetProperty("publicOrigin").GetString());await SetOrigin(s.Api,"https://after.test");
        var retries=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/publish",key:key)));try{foreach(var response in retries){response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(original.RootElement.GetProperty("accessContext").GetRawText(),json.RootElement.GetProperty("accessContext").GetRawText());}}finally{foreach(var response in retries)response.Dispose();}Assert.Equal(1L,await Count(s.Api));
    }
    [Fact] public async Task AddressEditDoesNotRewriteReleaseHistoryOrCandidateAndSnapshot()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await SetOrigin(s.Api,"https://before.test");var id=await s.ReadyAsync();byte[] candidate;await using(var db=s.Api.Context())candidate=(await db.Set<ReleaseRecord>().SingleAsync()).CandidateBytes!;
        await s.BuildAsync(id);var original=(await s.DesiredAsync(id)).Payload;await SetOrigin(s.Api,"https://after.test");using var detail=await Release(s.Api,id);var r=detail.RootElement;
        Assert.Equal("https://before.test",r.GetProperty("accessContext").GetProperty("publicOrigin").GetString());Assert.Equal("https://after.test",r.GetProperty("currentPublicOrigin").GetString());Assert.True(r.GetProperty("accessAddressChanged").GetBoolean());
        await using var check=s.Api.Context();Assert.Equal(candidate,(await check.Set<ReleaseRecord>().SingleAsync()).CandidateBytes);Assert.Equal(original,(await s.DesiredAsync(id)).Payload);Assert.DoesNotContain("before.test",System.Text.Encoding.UTF8.GetString(original));Assert.DoesNotContain("accessAddressRevision",System.Text.Encoding.UTF8.GetString(original));Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)),(await check.Set<GatewayConfigVersion>().SingleAsync()).SnapshotHash);
    }
    [Fact] public async Task LegacyHistoryRemainsUnknownAndReadScopeNeverSeesInternalEntry()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await SetOrigin(s.Api,"https://current.test");var legacy=new ReleaseRecord{EnvironmentId=s.Api.Environment.Id,ReleaseNo="LEGACY",ReleaseType="publish",Status="Succeeded",RequestedBy=s.Api.User.Id};await using(var db=s.Api.Context()){db.Add(legacy);await db.SaveChangesAsync();}
        using var old=await Release(s.Api,legacy.Id);Assert.Equal(JsonValueKind.Null,old.RootElement.GetProperty("accessContext").ValueKind);Assert.False(old.RootElement.GetProperty("accessAddressChanged").GetBoolean());
        var id=await s.PublishAsync();await using(var db=s.Api.Context()){await db.Set<UserProjectScope>().ExecuteUpdateAsync(p=>p.SetProperty(x=>x.AccessMode,"read"));}using var read=await Release(s.Api,id);Assert.DoesNotContain("internalOrigin",read.RootElement.GetRawText());Assert.DoesNotContain("private.test",read.RootElement.GetRawText());Assert.Equal(1L,await Count(s.Api));
    }
    [Fact] public async Task RecoveryCapturesNewEntryAndRetainsOriginalContext()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await SetOrigin(s.Api,"https://first.test");var failed=await s.PublishAsync(false);await using(var db=s.Api.Context()){await db.Set<ReleaseRecord>().Where(x=>x.Id==failed).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Status,"Failed").SetProperty(r=>r.FailureCode,"ack_timeout"));}await SetOrigin(s.Api,"https://recovery.test");
        using var retry=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{failed}/retry");retry.EnsureSuccessStatusCode();using var next=JsonDocument.Parse(await retry.Content.ReadAsStringAsync());Assert.Equal("https://recovery.test",next.RootElement.GetProperty("accessContext").GetProperty("publicOrigin").GetString());Assert.Equal(failed,next.RootElement.GetProperty("recoveryOf").GetGuid());using var old=await Release(s.Api,failed);Assert.Equal("https://first.test",old.RootElement.GetProperty("accessContext").GetProperty("publicOrigin").GetString());Assert.Equal(2L,await Count(s.Api));
    }
    [Fact] public async Task RollbackCapturesCurrentEntry()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await SetOrigin(s.Api,"https://first.test");await s.PublishAsync();var second=await s.PublishAsync();await SetOrigin(s.Api,"https://rollback.test");using var created=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{second}/rollback",new {targetConfigVersion=1L});created.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await created.Content.ReadAsStringAsync());var id=json.RootElement.GetProperty("id").GetGuid();using var submitted=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/submit");submitted.EnsureSuccessStatusCode();await s.ApproveAsync(id);await s.BuildAsync(id);using var read=await Release(s.Api,id);Assert.Equal("https://rollback.test",read.RootElement.GetProperty("accessContext").GetProperty("publicOrigin").GetString());Assert.Equal(3L,await Count(s.Api));
    }
    [Fact] public async Task FailedStartLeavesNoContext()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();var id=await s.ReadyAsync();await using(var db=s.Api.Context()){await db.Set<GatewayNode>().ExecuteUpdateAsync(p=>p.SetProperty(x=>x.LastHeartbeatAt,DateTimeOffset.UtcNow.AddHours(-1)));}
        using var response=await ApiFixture.CommandAsync(s.Api.Client,$"/api/v1/releases/{id}/publish");Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);using var read=await Release(s.Api,id);Assert.Equal(JsonValueKind.Null,read.RootElement.GetProperty("accessContext").ValueKind);Assert.Equal(0L,await Count(s.Api));
    }
    [Fact] public async Task CurrentEntryTracksPrefixOnlyChangesAndKeepsHistoricalPrefix()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await SetOrigin(s.Api,"https://api.test");var id=await s.PublishAsync();
        using(var same=await Release(s.Api,id)){Assert.Equal("/gateway",same.RootElement.GetProperty("currentBasePath").GetString());Assert.False(same.RootElement.GetProperty("accessAddressChanged").GetBoolean());}
        await SetOrigin(s.Api,"https://api.test","/new-entry");using var changed=await Release(s.Api,id);Assert.Equal("/gateway",changed.RootElement.GetProperty("accessContext").GetProperty("basePath").GetString());Assert.Equal("/new-entry",changed.RootElement.GetProperty("currentBasePath").GetString());Assert.True(changed.RootElement.GetProperty("accessAddressChanged").GetBoolean());
    }

}

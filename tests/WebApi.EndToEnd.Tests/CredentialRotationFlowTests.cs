using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;

// Requires the same dedicated owner/context as the full SDK suite. Missing context fails; never skips.
public sealed class CredentialRotationFlowTests
{
    [Fact]
    public async Task Real_dual_key_rotation_preserves_unrelated_resources_and_counts_authorized_calls()
    {
        var contextFile=Environment.GetEnvironmentVariable("WEBAPI_E2E_CONTEXT_FILE");Assert.NotNull(contextFile);
        using var contextDocument=JsonDocument.Parse(await File.ReadAllBytesAsync(contextFile));var context=contextDocument.RootElement;
        var owner=Environment.GetEnvironmentVariable("WEBAPI_E2E_OWNER");Assert.False(string.IsNullOrWhiteSpace(owner));var source=Environment.GetEnvironmentVariable("WEBAPI_SOURCE_REVISION");Assert.Matches("^[a-f0-9]{40}$",source!);
        using var admin=Client();using var approver=Client();using var security=Client();await Login(admin,"e2e-admin");await Login(approver,"e2e-approver");await Login(security,"e2e-security");
        using var a=new HttpClient{BaseAddress=new("http://gateway-a:8080")};using var b=new HttpClient{BaseAddress=new("http://gateway-b:8080")};
        var env=context.GetProperty("environmentId").GetString()!;var project=context.GetProperty("projectId").GetString()!;var organization=context.GetProperty("organizationId").GetString()!;
        var suffix=Guid.NewGuid().ToString("N")[..16];var routePath="/credential-sdk-"+suffix;var started=DateTimeOffset.UtcNow;var hour=new DateTimeOffset(started.Year,started.Month,started.Day,started.Hour,0,0,TimeSpan.Zero);
        var application=await Write($"/applications",new{organizationId=organization,projectId=project,code="ROT"+suffix,name="SDK独立轮换",owner="SDK owner",status="Active"});var appId=application.GetProperty("id").GetString()!;
        var api=await Write($"/projects/{project}/apis",new{code="ROT"+suffix,name="SDK独立轮换API",lifecycleStatus="Draft"});var apiId=api.GetProperty("id").GetString()!;
        var cluster=await Write($"/environments/{env}/clusters",new{name="SDK rotation "+suffix,loadBalancingPolicy="RoundRobin",healthCheckEnabled=false,healthCheckPath="/health",healthCheckIntervalSec=30,status="Active"});var clusterId=cluster.GetProperty("id").GetString()!;
        await Write($"/clusters/{clusterId}/destinations",new{name="SDK backend",address="http://backend-a:8080/",weight=1,enabled=true});
        var validFrom=started.AddMinutes(-1);var expiresAt=started.AddDays(1);
        await Write($"/applications/{appId}/permissions",new{apiId,environmentId=env,validFrom,expiresAt});
        var old=await Write($"/applications/{appId}/credentials",new{validFrom,expiresAt});var oldId=old.GetProperty("credential").GetProperty("id").GetString()!;var oldKey=old.GetProperty("apiKey").GetString()!;
        var beforeEnvironment=await Get($"/environments/{env}");var baselineNumber=beforeEnvironment.GetProperty("desiredConfigVersion");JsonElement? unrelated=baselineNumber.ValueKind==JsonValueKind.Null?null:await Get($"/environments/{env}/snapshots/{baselineNumber.GetInt64()}");
        string? routeId=null;var version=0;
        var baseline=await Publish();await CallBoth(oldKey,HttpStatusCode.OK);
        var next=await Write($"/applications/{appId}/credentials",new{validFrom,expiresAt});var nextId=next.GetProperty("credential").GetProperty("id").GetString()!;var nextKey=next.GetProperty("apiKey").GetString()!;
        await CallBoth(nextKey,HttpStatusCode.Unauthorized);
        var first=await Publish();await CallBoth(oldKey,HttpStatusCode.OK);await CallBoth(nextKey,HttpStatusCode.OK);await Proof(first,"Overlap");
        var currentOld=await Get($"/credentials/{oldId}");var etag="\""+currentOld.GetProperty("revision").GetInt64()+"\"";
        var change=new{validFrom=currentOld.GetProperty("validFrom").GetDateTimeOffset(),expiresAt=currentOld.GetProperty("expiresAt").GetDateTimeOffset(),status="Revoked"};
        var impact=await Write($"/credentials/{oldId}/impact",change,etag);
        await Write($"/credentials/{oldId}",new{change.validFrom,change.expiresAt,change.status,impactConfirmation=new{impactHash=impact.GetProperty("impactHash").GetString(),confirmCloseAllAccess=true}},etag,"PUT");
        await CallBoth(oldKey,HttpStatusCode.OK); // Working revocation does not rewrite the already published snapshot.
        var second=await Publish();await CallBoth(oldKey,HttpStatusCode.Unauthorized);await CallBoth(nextKey,HttpStatusCode.OK);var finalProof=await Proof(second,"Revoked");
        Assert.NotEqual(first.GetProperty("id").GetString(),second.GetProperty("id").GetString());Assert.True(second.GetProperty("targetConfigVersion").GetInt64()>first.GetProperty("targetConfigVersion").GetInt64());Assert.True(second.GetProperty("deploymentSequence").GetInt64()>first.GetProperty("deploymentSequence").GetInt64());
        var after=await Get($"/environments/{env}/snapshots/{second.GetProperty("targetConfigVersion").GetInt64()}");if(unrelated is JsonElement original)
        {
            foreach(var name in new[]{"routes","applications"})foreach(var resource in original.GetProperty(name).EnumerateArray())
            {var id=resource.GetProperty("id").GetGuid();var actual=after.GetProperty(name).EnumerateArray().Single(x=>x.GetProperty("id").GetGuid()==id);Assert.Equal(resource.GetRawText(),actual.GetRawText());}
        }
        var revisionBefore=(await Get($"/applications/{appId}")).GetProperty("application").GetProperty("revision").GetInt64();
        JsonElement collected=default;var deadline=DateTimeOffset.UtcNow.AddSeconds(60);
        while(DateTimeOffset.UtcNow<deadline)
        {
            var end=DateTimeOffset.UtcNow;end=new(end.Year,end.Month,end.Day,end.Hour,0,0,TimeSpan.Zero);end=end.AddHours(1);
            collected=await Get($"/applications/{appId}/credential-usage?environmentId={env}&from={Uri.EscapeDataString(hour.ToString("O"))}&to={Uri.EscapeDataString(end.ToString("O"))}&page=1&pageSize=100");var items=collected.GetProperty("page").GetProperty("items").EnumerateArray().ToArray();
            if(items.Single(x=>x.GetProperty("credentialId").GetString()==oldId).GetProperty("count").GetString()=="6"&&items.Single(x=>x.GetProperty("credentialId").GetString()==nextId).GetProperty("count").GetString()=="4")break;
            await Task.Delay(500);
        }
        var counts=collected.GetProperty("page").GetProperty("items").EnumerateArray().ToArray();Assert.Equal("6",counts.Single(x=>x.GetProperty("credentialId").GetString()==oldId).GetProperty("count").GetString());Assert.Equal("4",counts.Single(x=>x.GetProperty("credentialId").GetString()==nextId).GetProperty("count").GetString());
        Assert.Equal(revisionBefore,(await Get($"/applications/{appId}")).GetProperty("application").GetProperty("revision").GetInt64());Assert.NotEqual(JsonValueKind.Null,(await Get($"/credentials/{nextId}")).GetProperty("lastUsedAt").ValueKind);
        Directory.CreateDirectory("/workspace/docs/evidence/account-credential");await File.WriteAllTextAsync("/workspace/docs/evidence/account-credential/rotation.json",JsonSerializer.Serialize(new{sourceRevision=source,ownerId=owner,scenario="RealDualKeyRotation",verifiedAt=DateTimeOffset.UtcNow,firstReleaseId=first.GetProperty("id").GetString(),secondReleaseId=second.GetProperty("id").GetString(),payloadHash=finalProof.GetProperty("payloadHash").GetString(),oldAuthorizedCount="6",newAuthorizedCount="4",unrelatedSnapshotResourcesPreserved=true}));

        HttpClient Client()=>new(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=new("http://control-plane:8080")};
        async Task Login(HttpClient client,string name){var password=context.TryGetProperty("passwordFiles",out var files)&&files.TryGetProperty(name,out var file)?file.GetString()!:context.GetProperty("passwordFile").GetString()!;var identity=await ExternalScenario.WriteAsync(client,"/auth/login",new{username=name,password=await File.ReadAllTextAsync(password)});Assert.False(identity.GetProperty("mustChangePassword").GetBoolean());}
        Task<JsonElement> Write(string path,object body,string? tag=null,string method="POST")=>ExternalScenario.WriteAsync(admin,path,body,tag,method);
        async Task<JsonElement> Get(string path){using var response=await admin.GetAsync("/api/v1"+path);response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<JsonElement>();}
        async Task CallBoth(string key,HttpStatusCode status){foreach(var client in new[]{a,b}){using var request=new HttpRequestMessage(HttpMethod.Get,routePath);request.Headers.Add("X-API-Key",key);using var response=await client.SendAsync(request);Assert.Equal(status,response.StatusCode);if(status==HttpStatusCode.OK)Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receivedApiKey").GetBoolean());}}
        async Task<JsonElement> Publish()
        {
            version++;var current=await Write($"/apis/{apiId}/versions",new{version=version+".0.0",changeType="compatible"});var body=new{apiVersionId=current.GetProperty("id").GetString(),routeName="SDK rotation",path=routePath,methods=new[]{"GET"},clusterId,priority=100,enabled=true,timeoutMs=30000};
            if(routeId is null)routeId=(await Write($"/environments/{env}/routes",body)).GetProperty("id").GetString();else{var route=await Get($"/routes/{routeId}");await Write($"/routes/{routeId}",body,"\""+route.GetProperty("revision").GetInt64()+"\"","PUT");}
            var environment=await Get($"/environments/{env}");var number=environment.GetProperty("desiredConfigVersion");var baselineVersion=number.ValueKind==JsonValueKind.Null?0:number.GetInt64();var versionIds=new[]{current.GetProperty("id").GetString()};var preview=await Write($"/environments/{env}/releases/preview",new{baseConfigVersion=baselineVersion,versionIds});
            var release=await Write($"/environments/{env}/releases",new{baseConfigVersion=baselineVersion,versionIds,resourceRevisions=preview.GetProperty("resourceRevisions")});var id=release.GetProperty("id").GetString()!;
            await ExternalScenario.WriteAsync(admin,$"/releases/{id}/submit");await ExternalScenario.WriteAsync(approver,$"/releases/{id}/approve",new{comment="SDK独立API审核"});await ExternalScenario.WriteAsync(security,$"/releases/{id}/approve",new{comment="SDK独立安全审核"});await ExternalScenario.WriteAsync(admin,$"/releases/{id}/publish");
            var until=DateTimeOffset.UtcNow.AddSeconds(40);while(DateTimeOffset.UtcNow<until){release=await Get($"/releases/{id}");if(release.GetProperty("state").GetString()=="Succeeded"){Assert.Equal(2,release.GetProperty("targets").GetArrayLength());Assert.All(release.GetProperty("targets").EnumerateArray(),t=>Assert.True(t.GetProperty("acknowledged").GetBoolean()));return release;}Assert.DoesNotContain(release.GetProperty("state").GetString(),new[]{"Failed","Rejected","Cancelled"});await Task.Delay(250);}Assert.Fail("Real rotation release did not converge.");return default;
        }
        async Task<JsonElement> Proof(JsonElement release,string stage)
        {var until=DateTimeOffset.UtcNow.AddSeconds(20);while(DateTimeOffset.UtcNow<until){using var response=await admin.GetAsync($"/api/v1/applications/{appId}/credential-rollout?environmentId={env}&releaseId={release.GetProperty("id").GetString()}&oldCredentialId={oldId}&newCredentialId={nextId}&stage={stage}");if(response.IsSuccessStatusCode)return await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);await Task.Delay(250);}Assert.Fail("Fresh current dual-node proof did not converge.");return default;}
    }
}

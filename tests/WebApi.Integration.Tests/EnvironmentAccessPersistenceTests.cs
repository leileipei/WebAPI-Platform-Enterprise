using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class EnvironmentAccessPersistenceTests
{
    private static string PathFor(ApiFixture api)=>$"/api/v1/environments/{api.Environment.Id}";
    private static Dictionary<string,object?> Body()=>new() { ["code"]="TEST",["name"]="测试新版",["status"]="Active",["isProduction"]=false,["sortOrder"]=0,["releasePolicyId"]=null };
    private static async Task Configure(ApiFixture api)
    {
        var body=Body();body["gatewayPublicUrl"]="HTTPS://API.TEST:443/";body["gatewayInternalUrl"]="http://private.example:8080/";body["basePath"]="/gateway/";
        using var saved=await api.WriteAsync(HttpMethod.Put,PathFor(api),body,"\"1\"");saved.EnsureSuccessStatusCode();
    }
    private static async Task<JsonDocument> Detail(ApiFixture api)
    {
        using var response=await api.Client.GetAsync(PathFor(api));response.EnsureSuccessStatusCode();return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    [Fact] public async Task OmittedPropertiesPreserveSavedAddress()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();await Configure(api);
        using var saved=await api.WriteAsync(HttpMethod.Put,PathFor(api),Body(),"\"2\"");saved.EnsureSuccessStatusCode();
        using var detail=await Detail(api);var e=detail.RootElement;
        Assert.Equal("https://api.test",e.GetProperty("gatewayPublicUrl").GetString());Assert.Equal("http://private.example:8080",e.GetProperty("gatewayInternalUrl").GetString());Assert.Equal("/gateway",e.GetProperty("basePath").GetString());Assert.Equal(2,e.GetProperty("accessAddressRevision").GetInt64());Assert.Equal(3,e.GetProperty("revision").GetInt64());
    }
    [Fact] public async Task ExplicitNullClearsOnlySelectedOrigin()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();await Configure(api);var body=Body();body["gatewayPublicUrl"]=null;
        using var saved=await api.WriteAsync(HttpMethod.Put,PathFor(api),body,"\"2\"");saved.EnsureSuccessStatusCode();using var detail=await Detail(api);var e=detail.RootElement;
        Assert.Equal(JsonValueKind.Null,e.GetProperty("gatewayPublicUrl").ValueKind);Assert.Equal("http://private.example:8080",e.GetProperty("gatewayInternalUrl").GetString());Assert.Equal(3,e.GetProperty("accessAddressRevision").GetInt64());
    }
    [Fact] public async Task ReadScopeNeverReceivesInternalOriginAndRevocationIsImmediate()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();await Configure(api);
        foreach(var path in new[]{"/api/v1/scope-tree",$"/api/v1/projects/{api.Project.Id}/environments"}) {using var response=await api.Client.GetAsync(path);var json=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("gatewayInternalUrl",json);Assert.DoesNotContain("private.example",json);Assert.Contains("https://api.test",json);}
        await using(var db=api.Context()){await db.Set<UserProjectScope>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.AccessMode,"read"));}
        using var detail=await Detail(api);Assert.False(detail.RootElement.TryGetProperty("gatewayInternalUrl",out _));Assert.DoesNotContain("private.example",detail.RootElement.GetRawText());
        await using(var db=api.Context()){await db.Set<UserProjectScope>().ExecuteDeleteAsync();}
        using var hidden=await api.Client.GetAsync(PathFor(api));Assert.Equal(HttpStatusCode.NotFound,hidden.StatusCode);
    }
    [Fact] public async Task ProductionFlagValidatesExistingOriginWithoutDatabaseOrAuditSideEffects()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();var body=Body();body["gatewayPublicUrl"]="http://api.test";
        using var saved=await api.WriteAsync(HttpMethod.Put,PathFor(api),body,"\"1\"");saved.EnsureSuccessStatusCode();body=Body();body["isProduction"]=true;
        using var rejected=await api.WriteAsync(HttpMethod.Put,PathFor(api),body,"\"2\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,rejected.StatusCode);
        await using var db=api.Context();Assert.False((await db.Set<EnvironmentRecord>().SingleAsync()).IsProduction);Assert.Equal(1,await db.Set<AuditLog>().CountAsync(x=>x.Action=="environment.update"));
    }
    [Fact] public async Task EquivalentNormalizationDoesNotIncrementAccessRevisionAndStaleWriteDoesNotAudit()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();await Configure(api);var body=Body();body["gatewayPublicUrl"]="https://api.test/";body["basePath"]="/gateway";
        using var saved=await api.WriteAsync(HttpMethod.Put,PathFor(api),body,"\"2\"");saved.EnsureSuccessStatusCode();using var stale=await api.WriteAsync(HttpMethod.Put,PathFor(api),body,"\"2\"");Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        using var detail=await Detail(api);Assert.Equal(2,detail.RootElement.GetProperty("accessAddressRevision").GetInt64());await using var db=api.Context();Assert.Equal(2,await db.Set<AuditLog>().CountAsync(x=>x.Action=="environment.update"));
    }
    [Fact] public async Task CreationNormalizesAndOldCreationRetainsUnknownDefaults()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();
        using var created=await api.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{api.Project.Id}/environments",new {code="UAT",name="验收",gatewayPublicUrl="https://uat.test/",basePath="/gateway/"});created.EnsureSuccessStatusCode();using var value=JsonDocument.Parse(await created.Content.ReadAsStringAsync());Assert.Equal("https://uat.test",value.RootElement.GetProperty("gatewayPublicUrl").GetString());Assert.Equal("/gateway",value.RootElement.GetProperty("basePath").GetString());Assert.Equal(1,value.RootElement.GetProperty("accessAddressRevision").GetInt64());
        using var legacy=await api.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{api.Project.Id}/environments",new {code="DEV",name="开发"});legacy.EnsureSuccessStatusCode();using var old=JsonDocument.Parse(await legacy.Content.ReadAsStringAsync());Assert.Equal("/",old.RootElement.GetProperty("basePath").GetString());Assert.Equal(JsonValueKind.Null,old.RootElement.GetProperty("gatewayPublicUrl").ValueKind);
    }
}

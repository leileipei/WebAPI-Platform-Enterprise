using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class EnvironmentApiAccessTests
{
    private const string Document="""{"openapi":"3.0.3","info":{"title":"Orders","version":"1"},"servers":[{"url":"https://old-source.test"}],"paths":{"/orders":{"get":{"operationId":"orders","responses":{"200":{"description":"ok"}}}}}}""";
    private static string Entry(ApiFixture api,string suffix="access-addresses",string query="view=working")=>$"/api/v1/environments/{api.Environment.Id}/apis/{api.Api.Id}/{suffix}?{query}";
    private static async Task Configure(ApiFixture api)
    {using var response=await api.Client.GetAsync($"/api/v1/environments/{api.Environment.Id}");using var current=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var e=current.RootElement;using var saved=await api.WriteAsync(HttpMethod.Put,$"/api/v1/environments/{api.Environment.Id}",new {code="TEST",name="测试",status="Active",isProduction=e.GetProperty("isProduction").GetBoolean(),releasePolicyId=e.GetProperty("releasePolicyId").ValueKind==JsonValueKind.Null?(Guid?)null:e.GetProperty("releasePolicyId").GetGuid(),sortOrder=0,gatewayPublicUrl="https://api.test",gatewayInternalUrl="http://private.test",basePath="/gateway"},response.Headers.ETag!.ToString());saved.EnsureSuccessStatusCode();}
    private static async Task<ApiFixture> Working(bool configured=true)
    {
        var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();await using(var db=api.Context()){var v=await db.Set<ApiVersion>().SingleAsync();v.OpenapiDocument=Document;v.OpenapiSource=Document;v.SchemaHash="unchanged";await db.SaveChangesAsync();}
        using var route=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/orders"));route.EnsureSuccessStatusCode();if(configured)await Configure(api);return api;
    }
    [Fact] public async Task WorkingReturnsEveryActualMethodWithPublicOnlyReadProjection()
    {
        await using var api=await Working();using var extra=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/orders/{id}","POST"));extra.EnsureSuccessStatusCode();
        await using(var db=api.Context()){await db.Set<UserProjectScope>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.AccessMode,"read"));}
        using var response=await api.Client.GetAsync(Entry(api));response.EnsureSuccessStatusCode();Assert.Equal("no-store",response.Headers.CacheControl?.ToString());using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var rows=json.RootElement.GetProperty("routes");Assert.Equal(2,rows.GetArrayLength());Assert.Contains(rows.EnumerateArray(),r=>r.GetProperty("publicTemplate").GetString()=="https://api.test/gateway/orders/{id}"&&r.GetProperty("method").GetString()=="POST");Assert.DoesNotContain("internalTemplate",json.RootElement.GetRawText());Assert.DoesNotContain("private.test",json.RootElement.GetRawText());Assert.DoesNotContain("Authorization",json.RootElement.GetRawText());
    }
    [Fact] public async Task DocumentCopyLeavesStoredContractUntouched()
    {
        await using var api=await Working();string before;await using(var initial=api.Context())before=(await initial.Set<ApiVersion>().SingleAsync()).OpenapiDocument!;using var response=await api.Client.GetAsync(Entry(api,"openapi"));response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("https://api.test/gateway",json.RootElement.GetProperty("servers")[0].GetProperty("url").GetString());Assert.True(json.RootElement.GetProperty("paths").TryGetProperty("/orders",out _));
        await using var db=api.Context();var version=await db.Set<ApiVersion>().SingleAsync();Assert.Equal(before,version.OpenapiDocument);Assert.Equal(Document,version.OpenapiSource);Assert.Equal("unchanged",version.SchemaHash);Assert.Equal(1,version.Revision);
    }
    [Fact] public async Task NoConfiguredOriginNeverInventsAnEntry()
    {
        await using var api=await Working(false);using var response=await api.Client.GetAsync(Entry(api));response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.False(json.RootElement.GetProperty("configured").GetBoolean());Assert.Equal(JsonValueKind.Null,json.RootElement.GetProperty("routes")[0].GetProperty("publicTemplate").ValueKind);
        using var doc=await api.Client.GetAsync(Entry(api,"openapi"));Assert.Equal(HttpStatusCode.Conflict,doc.StatusCode);Assert.Contains("access_address_unconfigured",await doc.Content.ReadAsStringAsync());
    }
    [Fact] public async Task AmbiguousOrIncompleteMappingDoesNotReturnPartialDocument()
    {
        await using var api=await Working();using var extra=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/other"));extra.EnsureSuccessStatusCode();using var doc=await api.Client.GetAsync(Entry(api,"openapi"));Assert.Equal(HttpStatusCode.UnprocessableEntity,doc.StatusCode);Assert.Contains("ambiguous_route_contract",await doc.Content.ReadAsStringAsync());
    }
    [Fact] public async Task UnknownViewAndForeignScopeAreRejected()
    {
        await using var api=await Working();using var view=await api.Client.GetAsync(Entry(api,query:"view=latest"));Assert.Equal(HttpStatusCode.UnprocessableEntity,view.StatusCode);
        await using(var db=api.Context()){var o=new Organization{Code="FOREIGN",Name="外部"};var p=new Project{OrganizationId=o.Id,Code="P2",Name="外部项目"};var a=new Api{OrganizationId=o.Id,ProjectId=p.Id,Code="API2",Name="外部",OwnerUserId=api.User.Id};db.AddRange(o,p,a);await db.SaveChangesAsync();using var foreign=await api.Client.GetAsync($"/api/v1/environments/{api.Environment.Id}/apis/{a.Id}/access-addresses?view=working");Assert.Equal(HttpStatusCode.NotFound,foreign.StatusCode);}
    }
    [Fact] public async Task RunningUsesSnapshotAfterWorkingRouteDeletionAndDoesNotSubstituteVersion()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await Configure(s.Api);await using(var db=s.Api.Context()){var v=await db.Set<ApiVersion>().SingleAsync();v.OpenapiDocument=Document;await db.SaveChangesAsync();}await s.PublishAsync();
        Guid routeId;await using(var db=s.Api.Context())routeId=(await db.Set<ApiRoute>().SingleAsync()).Id;using(var current=await s.Api.Client.GetAsync($"/api/v1/routes/{routeId}")){using var removed=await s.Api.WriteAsync(HttpMethod.Delete,$"/api/v1/routes/{routeId}",etag:current.Headers.ETag!.ToString());removed.EnsureSuccessStatusCode();}
        using var running=await s.Api.Client.GetAsync(Entry(s.Api,query:""));running.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await running.Content.ReadAsStringAsync());Assert.Equal("https://api.test/gateway/orders",json.RootElement.GetProperty("routes")[0].GetProperty("publicTemplate").GetString());Assert.Equal(1,json.RootElement.GetProperty("routes")[0].GetProperty("runningConfigVersion").GetInt64());
        using var working=await s.Api.Client.GetAsync(Entry(s.Api));working.EnsureSuccessStatusCode();using var empty=JsonDocument.Parse(await working.Content.ReadAsStringAsync());Assert.Equal(0,empty.RootElement.GetProperty("routes").GetArrayLength());
        using var mismatch=await s.Api.Client.GetAsync(Entry(s.Api,query:$"versionId={Guid.NewGuid()}"));Assert.Equal(HttpStatusCode.NotFound,mismatch.StatusCode);
        using var doc=await s.Api.Client.GetAsync(Entry(s.Api,"openapi",""));doc.EnsureSuccessStatusCode();Assert.Contains("https://api.test/gateway",await doc.Content.ReadAsStringAsync());
    }
    [Fact] public async Task ImportedVersionUsesOnlyItsSelectedOperationFromSharedSourceBundle()
    {
        await using var api=await ImportSessionTests.Setup();await Configure(api);var preview=await ImportSessionTests.Preview(api);using var committed=await api.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{preview.PreviewId}/commit",ImportSessionTests.Commit(preview));committed.EnsureSuccessStatusCode();
        Guid importedId;await using(var db=api.Context())importedId=(await db.Set<Api>().SingleAsync(x=>x.Code=="SESSION")).Id;
        using var response=await api.Client.GetAsync($"/api/v1/environments/{api.Environment.Id}/apis/{importedId}/openapi?view=working");response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Single(json.RootElement.GetProperty("paths").EnumerateObject());Assert.True(json.RootElement.GetProperty("paths").TryGetProperty("/import/one",out _));
    }
    [Fact] public async Task DesiredSnapshotIsNotAdvertisedAsNodeConfirmedRunning()
    {
        await using var s=new DeploymentScenario();await s.InitializeAsync();await Configure(s.Api);await s.PublishAsync(false);using var response=await s.Api.Client.GetAsync(Entry(s.Api,query:""));Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("running_state_unconfirmed",await response.Content.ReadAsStringAsync());
    }
}

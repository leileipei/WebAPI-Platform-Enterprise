using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class CatalogTests
{
    [Fact] public async Task SameShapeMethodsConflict()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var first=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/orders/{id}"));Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        using var duplicate=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/orders/{code}"));Assert.Equal(HttpStatusCode.Conflict,duplicate.StatusCode);
        using var otherMethod=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/orders/{code}","POST"));Assert.Equal(HttpStatusCode.OK,otherMethod.StatusCode);
        await using var db=api.Context();Assert.Equal(2,await db.Set<ApiRoute>().CountAsync());Assert.Equal(2,await db.Set<RouteMethod>().CountAsync());
    }
    [Fact] public async Task ConcurrentMethodPathCreationHasSingleWinner()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        // Acquire one token before launching independent HTTP commands, avoiding a concurrent cookie refresh.
        var token=await api.CsrfAsync();async Task<HttpResponseMessage> Create(string path) {using var req=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes"){Content=JsonContent.Create(api.RouteBody(path))};req.Headers.Add("X-CSRF-Token",token);return await api.Client.SendAsync(req);}
        var results=await Task.WhenAll(Create("/parallel/{id}"),Create("/parallel/{code}"));try {Assert.Equal(1,results.Count(r=>r.StatusCode==HttpStatusCode.OK));Assert.Equal(1,results.Count(r=>r.StatusCode==HttpStatusCode.Conflict));}finally {foreach(var r in results) r.Dispose();}
        await using var db=api.Context();Assert.Equal(1,await db.Set<ApiRoute>().CountAsync());
    }
    [Fact] public async Task StaticRouteHasDeterministicPriority()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var dynamic=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/orders/{id}"));Assert.Equal(HttpStatusCode.OK,dynamic.StatusCode);
        using var search=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/orders/search"));Assert.Equal(HttpStatusCode.OK,search.StatusCode);
        var d=await dynamic.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();var s=await search.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();Assert.True(s.GetProperty("matchOrder").GetInt32()<d.GetProperty("matchOrder").GetInt32());
    }
    [Fact] public async Task PublishedVersionImmutable()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        await using(var db=api.Context()) {var version=await db.Set<ApiVersion>().SingleAsync();version.Status="Published";version.SealedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}
        using var result=await api.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{api.Version.Id}",new {version="1.0.0",changeType="breaking",openapiDocument="{}"},"\"1\"");Assert.Equal(HttpStatusCode.Conflict,result.StatusCode);
        await using var check=api.Context();Assert.Equal("compatible",(await check.Set<ApiVersion>().SingleAsync()).ChangeType);
    }
    [Fact] public async Task ForeignClusterBindingRejected()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        var foreignEnv=new EnvironmentRecord {ProjectId=api.Project.Id,Code="OTHER",Name="其他"};var foreign=new UpstreamCluster {ProjectId=api.Project.Id,EnvironmentId=foreignEnv.Id,Name="OtherBackend",LoadBalancingPolicy="RoundRobin",HealthCheckPath="/",HealthCheckIntervalSec=30};await using(var db=api.Context()) {db.AddRange(foreignEnv,foreign);await db.SaveChangesAsync();}
        using var result=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/orders",cluster:foreign.Id));Assert.Equal(HttpStatusCode.UnprocessableEntity,result.StatusCode);
        await using var check=api.Context();Assert.Empty(await check.Set<ApiRoute>().ToListAsync());
    }
    [Fact] public async Task LastDestinationProtected()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var result=await api.WriteAsync(HttpMethod.Delete,$"/api/v1/destinations/{api.Destination.Id}",etag:"\"1\"");Assert.Equal(HttpStatusCode.Conflict,result.StatusCode);
        await using var check=api.Context();Assert.Single(await check.Set<UpstreamDestination>().ToListAsync());
    }
    [Fact] public async Task EditingWorkingRouteLeavesPublishedSnapshotBytesUnchanged()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var create=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/old"));Assert.Equal(HttpStatusCode.OK,create.StatusCode);
        var bytes=Encoding.UTF8.GetBytes("{\"immutable\":true}");Guid routeId;
        await using(var db=api.Context()) {var config=new GatewayConfigVersion {EnvironmentId=api.Environment.Id,VersionNo=1,Status="Published",CreatedBy=api.User.Id};db.Add(config);db.Add(new GatewayConfigSnapshot {ConfigVersionId=config.Id,Payload=Encoding.UTF8.GetString(bytes),PayloadBytes=bytes,SizeBytes=bytes.Length});routeId=(await db.Set<ApiRoute>().SingleAsync()).Id;await db.SaveChangesAsync();}
        using var edit=await api.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{routeId}",api.RouteBody("/new",id:routeId),"\"1\"");Assert.Equal(HttpStatusCode.OK,edit.StatusCode);
        await using var check=api.Context();Assert.Equal("/new",(await check.Set<ApiRoute>().SingleAsync()).Path);Assert.Equal(bytes,(await check.Set<GatewayConfigSnapshot>().SingleAsync()).PayloadBytes);
    }
    [Theory] [InlineData("http://localhost:8080/")] [InlineData("http://user:secret@test-backend:8080/")] [InlineData("file:///etc/passwd")]
    public async Task DestinationMustMatchConfiguredNetworkAllowlist(string address)
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var result=await api.WriteAsync(HttpMethod.Post,$"/api/v1/clusters/{api.Cluster.Id}/destinations",new {name="forbidden",address,weight=1,enabled=true});Assert.Equal(HttpStatusCode.UnprocessableEntity,result.StatusCode);
        await using var check=api.Context();Assert.Single(await check.Set<UpstreamDestination>().ToListAsync());
    }
    [Theory] [InlineData("parameters")] [InlineData("schemas")] public async Task NullNestedInputIsRejectedWithoutVersionChange(string resource)
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var result=await api.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{api.Version.Id}/{resource}",new object?[]{null},"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,result.StatusCode);
        await using var db=api.Context();Assert.Equal(1,(await db.Set<ApiVersion>().SingleAsync()).Revision);Assert.Empty(await db.Set<ApiParameter>().ToListAsync());Assert.Empty(await db.Set<ApiSchema>().ToListAsync());
    }
}

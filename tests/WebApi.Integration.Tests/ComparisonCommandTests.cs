using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ComparisonCommandTests
{
    [Fact] public async Task Depth64EvidenceCanBeStoredAndReplayed()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();var schema="{\"type\":\"string\"}";for(var n=1;n<64;n++)schema="{\"type\":\"array\",\"items\":"+schema+"}";
        await using(var db=f.Api.Context()){db.Add(new ApiSchema {ApiVersionId=f.Target.Id,SchemaType="request",Name="Body",ContentType="application/json",SchemaJson=schema});await db.SaveChangesAsync();}
        var key=Guid.NewGuid().ToString("N");using var first=await f.CreateAsync(key);Assert.Equal(HttpStatusCode.OK,first.StatusCode);var body=await first.Content.ReadAsStringAsync();using var again=await f.CreateAsync(key);Assert.Equal(body,await again.Content.ReadAsStringAsync());var value=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=128}).RootElement;Assert.Equal("Complete",value.GetProperty("report").GetProperty("coverage").GetString());
    }
    [Theory] [InlineData("api.read")] [InlineData("api.version.read")] [InlineData("api.schema.read")]
    public async Task ReadPermissionsAreIndependent(string permission)
    {await using var f=new ComparisonFixture();await f.InitializeAsync();var id=await f.CreateIdAsync();await f.RevokeAsync(permission);using var create=await f.CreateAsync();Assert.Equal(HttpStatusCode.NotFound,create.StatusCode);using var get=await f.Api.Client.GetAsync($"/api/v1/version-comparisons/{id}");Assert.Equal(HttpStatusCode.NotFound,get.StatusCode);using var export=await f.Api.Client.GetAsync($"/api/v1/version-comparisons/{id}/export?format=json");Assert.Equal(HttpStatusCode.NotFound,export.StatusCode);using var list=await f.Api.Client.GetAsync(f.Path);Assert.Equal(HttpStatusCode.NotFound,list.StatusCode);}
    [Fact] public async Task ComparisonReplayReturnsOriginalAfterEdit()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();var key=Guid.NewGuid().ToString("N");using var first=await f.CreateAsync(key);Assert.Equal(HttpStatusCode.OK,first.StatusCode);var body=await first.Content.ReadAsStringAsync();var value=JsonDocument.Parse(body).RootElement;var id=value.GetProperty("id").GetGuid();Assert.True(value.GetProperty("report").GetProperty("counts").GetProperty("breaking").GetInt32()>0);
        using var edit=await f.Api.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Target.Id}",new {version="2.0.1",openapiDocument="{\"openapi\":\"3.0.3\",\"info\":{\"title\":\"Comparison replay\",\"version\":\"2.0.1\"},\"paths\":{}}"},"\"1\"");edit.EnsureSuccessStatusCode();
        using var replay=await f.CreateAsync(key);Assert.Equal(body,await replay.Content.ReadAsStringAsync());
        using var current=await f.Api.Client.GetAsync($"/api/v1/version-comparisons/{id}");current.EnsureSuccessStatusCode();Assert.Equal("Stale",(await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("freshness").GetString());
        await using var db=f.Api.Context();Assert.Equal(1,await db.Set<AuditLog>().CountAsync(x=>x.Action=="comparison.created"));
    }
    [Fact] public async Task RevokedUserCannotReplayOrExport()
    {await using var f=new ComparisonFixture();await f.InitializeAsync();var key=Guid.NewGuid().ToString("N");using var first=await f.CreateAsync(key);first.EnsureSuccessStatusCode();var id=(await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();await f.RevokeAsync("api.schema.read");using var replay=await f.CreateAsync(key);Assert.Equal(HttpStatusCode.NotFound,replay.StatusCode);using var export=await f.Api.Client.GetAsync($"/api/v1/version-comparisons/{id}/export?format=csv");Assert.Equal(HttpStatusCode.NotFound,export.StatusCode);}
    [Fact] public async Task SameKeyDifferentPairConflicts()
    {await using var f=new ComparisonFixture();await f.InitializeAsync();var key=Guid.NewGuid().ToString("N");using var first=await f.CreateAsync(key);first.EnsureSuccessStatusCode();using var other=await f.CreateAsync(key,f.Request(f.Target.Id,f.Api.Version.Id));Assert.Equal(HttpStatusCode.Conflict,other.StatusCode);}
    [Fact] public async Task ExpectedRevisionMismatchIs412()
    {await using var f=new ComparisonFixture();await f.InitializeAsync();using var r=await f.CreateAsync(request:f.Request(toRevision:2));Assert.Equal(HttpStatusCode.PreconditionFailed,r.StatusCode);await using var db=f.Api.Context();Assert.Equal(0,await db.Set<AuditLog>().CountAsync(x=>x.Action=="comparison.created"));}
    [Fact] public async Task ForeignApiAndVersionCannotBeProbed()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();var other=new Api {OrganizationId=f.Api.Organization.Id,ProjectId=f.Api.Project.Id,Code="FOREIGN",Name="foreign",OwnerUserId=f.Api.User.Id};var version=new ApiVersion {ApiId=other.Id,Version="1",CreatedBy=f.Api.User.Id};await using(var db=f.Api.Context()){db.AddRange(other,version);await db.SaveChangesAsync();}
        using var r=await f.CreateAsync(request:f.Request(to:version.Id));Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);using var same=await f.CreateAsync(request:f.Request(to:f.Api.Version.Id));Assert.Equal(HttpStatusCode.UnprocessableEntity,same.StatusCode);
        using var unknown=await f.CreateAsync(request:f.Request(to:Guid.NewGuid()));Assert.Equal(HttpStatusCode.NotFound,unknown.StatusCode);
    }
    [Fact] public async Task ComparisonCursorBindsApiAndReviewFilter()
    {
        await using var f=new ComparisonFixture();await f.InitializeAsync();for(var n=0;n<3;n++)await f.CreateIdAsync();using var first=await f.Api.Client.GetAsync(f.Path+"?limit=1");first.EnsureSuccessStatusCode();var v=await first.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(1,v.GetProperty("items").GetArrayLength());var cursor=Uri.EscapeDataString(v.GetProperty("nextCursor").GetString()!);
        using var next=await f.Api.Client.GetAsync(f.Path+"?limit=1&cursor="+cursor);next.EnsureSuccessStatusCode();Assert.NotEqual(v.GetProperty("items")[0].GetProperty("id").GetGuid(),(await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("id").GetGuid());
        using var changed=await f.Api.Client.GetAsync(f.Path+"?cursor="+cursor+"&reviewId="+Guid.NewGuid());Assert.Equal(HttpStatusCode.BadRequest,changed.StatusCode);using var invalid=await f.Api.Client.GetAsync(f.Path+"?cursor=broken");Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class VersionContractSourceTests
{
    private const string Yaml="openapi: 3.1.0\ninfo: {title: Test, version: '1'}\npaths: {}\ncomponents:\n  schemas:\n    Value: {type: integer}\n";
    [Fact] public async Task YamlSourceDownloadHasCorrectMimeAndJsonRepresentation()
    {
        var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;
        using var saved=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}",new{version="1.0.1",openapiDocument=SchemaValidationApiTests.Root,openapiSource=Yaml,sourceFormat="yaml"},"\"1\"");Assert.True(saved.IsSuccessStatusCode,await saved.Content.ReadAsStringAsync());
        using var raw=await f.Client.GetAsync($"/api/v1/versions/{f.Version.Id}/openapi");raw.EnsureSuccessStatusCode();Assert.Equal("application/yaml",raw.Content.Headers.ContentType!.MediaType);Assert.Equal(Yaml,await raw.Content.ReadAsStringAsync());
        using var json=await f.Client.GetAsync($"/api/v1/versions/{f.Version.Id}/openapi?representation=json");json.EnsureSuccessStatusCode();Assert.Equal("application/json",json.Content.Headers.ContentType!.MediaType);using var parsed=JsonDocument.Parse(await json.Content.ReadAsStringAsync());Assert.Equal("3.1.0",parsed.RootElement.GetProperty("openapi").GetString());
        await using var db=f.Context();var row=await db.Set<ApiVersionContractSources>().SingleAsync();Assert.Equal("Oas31",row.Dialect);Assert.Contains("yaml",row.SourcesJson);Assert.Equal(2,(await db.Set<ApiVersion>().SingleAsync()).Revision);
    }
    [Fact] public async Task InconsistentRawAndCanonicalVersionRollsBack()
    {var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}",new{version="changed",openapiDocument=SchemaValidationApiTests.Root,openapiSource=Yaml.Replace("integer","string"),sourceFormat="yaml"},"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);await using var db=f.Context();Assert.Equal(1,(await db.Set<ApiVersion>().SingleAsync()).Revision);Assert.Empty(await db.Set<ApiVersionContractSources>().ToArrayAsync());}
    [Fact] public async Task DirectRootReplacementInvalidatesOldExternalBundle()
    {
        await using var f=await ImportSessionTests.Setup();var text=ImportAndCredentialTests.Source.Replace("#/components/schemas/Order","schemas/order.json");var p=await ImportSessionTests.Preview(f,ImportSessionTests.Input(f,text,[new{name="schemas/order.json",content="{\"type\":\"integer\"}",format="json"}]));using var committed=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",ImportSessionTests.Commit(p));committed.EnsureSuccessStatusCode();Guid id;string oldHash;await using(var db=f.Context()){var row=await db.Set<ApiVersionContractSources>().SingleAsync();id=row.ApiVersionId;oldHash=row.BundleHash;}
        using var saved=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{id}",new{version="updated",openapiDocument=SchemaValidationApiTests.Root,openapiSource=SchemaValidationApiTests.Root,sourceFormat="json"},"\"1\"");saved.EnsureSuccessStatusCode();await using var read=f.Context();var current=await read.Set<ApiVersionContractSources>().SingleAsync();Assert.NotEqual(oldHash,current.BundleHash);Assert.DoesNotContain("schemas/order.json",current.BundleJson);Assert.Equal(2,(await read.Set<ApiVersion>().SingleAsync(x=>x.Id==id)).Revision);
    }
    [Theory][InlineData("sealed")][InlineData("revision")]
    public async Task RootReplacementHonorsRevisionAndSealing(string change)
    {var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;if(change=="sealed"){await using var db=f.Context();await db.Set<ApiVersion>().ExecuteUpdateAsync(x=>x.SetProperty(v=>v.SealedAt,DateTimeOffset.UtcNow));}using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}",new{version="changed",openapiSource=Yaml,sourceFormat="yaml"},change=="revision"?"\"0\"":"\"1\"");Assert.Equal(change=="sealed"?HttpStatusCode.Conflict:HttpStatusCode.PreconditionFailed,r.StatusCode);await using var check=f.Context();Assert.Empty(await check.Set<ApiVersionContractSources>().ToArrayAsync());}
    [Theory][InlineData("{\"type\":\"nonsense\"}")][InlineData("{\"$ref\":\"missing.json\"}")][InlineData("{\"required\":[1]}")]
    public async Task NewDamagedSchemaCannotSave(string schema)
    {var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;string before;await using(var initial=f.Context())before=(await initial.Set<ApiSchema>().SingleAsync()).SchemaJson;using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/schemas",new[]{new SaveSchemaRequest(s.Schema.Id,"component","Value",null,"application/json",schema)},"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);await using var db=f.Context();Assert.Equal(1,(await db.Set<ApiVersion>().SingleAsync()).Revision);Assert.Equal(before,(await db.Set<ApiSchema>().SingleAsync()).SchemaJson);}
    [Fact] public async Task NonMatchingExampleCanStillSaveDraft()
    {var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/schemas",new[]{new SaveSchemaRequest(s.Schema.Id,"component","Value",null,"application/json","{\"type\":\"integer\",\"minimum\":5}","0")},"\"1\"");Assert.True(r.IsSuccessStatusCode,await r.Content.ReadAsStringAsync());await using var db=f.Context();Assert.Equal("0",(await db.Set<ApiSchema>().SingleAsync()).ExampleJson);Assert.Equal(2,(await db.Set<ApiVersion>().SingleAsync()).Revision);Assert.Single(await db.Set<ApiVersionContractSources>().ToArrayAsync());using var validation=await f.WriteAsync(HttpMethod.Post,SchemaValidationApiTests.Path(f),SchemaValidationApiTests.Body(s.Schema.Id,"0",2));validation.EnsureSuccessStatusCode();Assert.True((await validation.Content.ReadAsStringAsync()).Contains("\"status\":\"Invalid\"",StringComparison.Ordinal),await validation.Content.ReadAsStringAsync());}
    [Theory][InlineData("delete")][InlineData("rename")]
    public async Task ReferencedComponentChangeListsAffectedPointers(string change)
    {
        var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;var consumer=new ApiSchema{ApiVersionId=f.Version.Id,SchemaType="response",Name="Response",StatusCode=200,ContentType="application/json",SchemaJson="{\"$ref\":\"#/components/schemas/Value\"}"};await using(var db=f.Context()){db.Add(consumer);await db.SaveChangesAsync();}
        var candidates=new List<SaveSchemaRequest>{new(consumer.Id,"response","Response",200,"application/json",consumer.SchemaJson)};if(change=="rename")candidates.Add(new(s.Schema.Id,"component","Renamed",null,"application/json",s.Schema.SchemaJson));
        using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/schemas",candidates,"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);using var problem=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.Equal("schema_reference_impact",problem.RootElement.GetProperty("code").GetString());Assert.Contains(problem.RootElement.GetProperty("issues").EnumerateArray(),x=>x.GetProperty("pointer").GetString()!.EndsWith("/$ref",StringComparison.Ordinal));await using var check=f.Context();Assert.Equal("Value",(await check.Set<ApiSchema>().SingleAsync(x=>x.Id==s.Schema.Id)).Name);Assert.Equal(1,(await check.Set<ApiVersion>().SingleAsync()).Revision);
    }
    [Fact] public async Task StoredBundleTamperingNeverReturnsValid()
    {
        var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;using var save=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}",new{version="1",openapiSource=Yaml,sourceFormat="yaml"},"\"1\"");save.EnsureSuccessStatusCode();await using(var db=f.Context())await db.Set<ApiVersionContractSources>().ExecuteUpdateAsync(x=>x.SetProperty(v=>v.BundleHash,new string('0',64)));
        using var r=await f.WriteAsync(HttpMethod.Post,SchemaValidationApiTests.Path(f),SchemaValidationApiTests.Body(s.Schema.Id,revision:2));Assert.True(r.IsSuccessStatusCode,await r.Content.ReadAsStringAsync());Assert.Contains("\"status\":\"Incomplete\"",await r.Content.ReadAsStringAsync());
    }
    [Fact] public async Task JsonDownloadKeepsRawRepresentation()
    {var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;using var r=await f.Client.GetAsync($"/api/v1/versions/{f.Version.Id}/openapi");r.EnsureSuccessStatusCode();Assert.Equal("application/json",r.Content.Headers.ContentType!.MediaType);Assert.Equal(SchemaValidationApiTests.Root,await r.Content.ReadAsStringAsync());}
    [Fact] public async Task AmbiguousComponentNamesCannotOverwriteAnotherDefinition()
    {
        var setup=await SchemaValidationApiTests.Setup();await using var f=setup.Fixture;
        var values=new[]{new SaveSchemaRequest(setup.Schema.Id,"component","Value",null,"application/json","{\"type\":\"integer\"}"),new SaveSchemaRequest(null,"component","Value",null,"application/problem+json","{\"type\":\"boolean\"}")};
        using var response=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/schemas",values,"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.Contains("ambiguous_component_definition",await response.Content.ReadAsStringAsync());await using var db=f.Context();Assert.Single(await db.Set<ApiSchema>().ToArrayAsync());Assert.Equal(1,(await db.Set<ApiVersion>().SingleAsync()).Revision);
    }

}

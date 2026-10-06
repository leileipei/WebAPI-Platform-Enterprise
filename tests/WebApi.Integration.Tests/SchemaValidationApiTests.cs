using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Infrastructure.Contracts;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SchemaValidationApiTests
{
    internal const string Root="{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"Test\",\"version\":\"1\"},\"paths\":{},\"components\":{\"schemas\":{\"Value\":{\"type\":\"integer\"}}}}";
    internal static async Task<(ApiFixture Fixture,ApiSchema Schema,ApiParameter Parameter)> Setup(string schema="{\"type\":\"integer\"}",string contentType="application/json")
    {
        var f=new ApiFixture();await f.InitializeAsync(b=>b.Services.AddSingleton(new SchemaValidationSettings(System.Environment.GetEnvironmentVariable("WEBAPI_RUNTIME_TOOL_DLL")??throw new InvalidOperationException("Fresh RuntimeTool missing"))));await f.SeedCatalogAsync();
        var s=new ApiSchema{ApiVersionId=f.Version.Id,SchemaType="component",Name="Value",ContentType=contentType,SchemaJson=schema};var p=new ApiParameter{ApiVersionId=f.Version.Id,Name="q",Location="query",DataType="integer",Schema="{\"type\":\"integer\"}"};
        await using(var db=f.Context()){var v=await db.Set<ApiVersion>().SingleAsync();v.OpenapiDocument=Root;v.OpenapiSource=Root;v.SourceFormat="json";db.AddRange(s,p);await db.SaveChangesAsync();}using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();return(f,s,p);
    }
    internal static string Path(ApiFixture f)=>$"/api/v1/versions/{f.Version.Id}/schema-validation";
    internal static object Body(Guid? schemaId,string example="0",long revision=1,bool draft=false,string? draftSchema=null,Guid? parameterId=null)=>new{expectedVersionRevision=revision,schemaId,parameterId,draft,draftSchemaJson=draftSchema,exampleJson=example,direction="request",formatMode="Annotation"};
    [Fact] public async Task SchemaReadOnlyCanValidateDraftWithoutWrites()
    {
        var setup=await Setup();await using var f=setup.Fixture;
        await using(var db=f.Context()){await db.Set<UserProjectScope>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.AccessMode,"read_only"));await db.Set<RolePermission>().Where(x=>db.Set<Permission>().Where(p=>p.Code=="api.schema.write").Select(p=>p.Id).Contains(x.PermissionId)).ExecuteDeleteAsync();}
        string before;int audits,rows;await using(var db=f.Context()){before=(await db.Set<ApiSchema>().SingleAsync()).SchemaJson;audits=await db.Set<AuditLog>().CountAsync();rows=await db.Set<IdempotencyRecord>().CountAsync();}
        using var response=await f.WriteAsync(HttpMethod.Post,Path(f),Body(setup.Schema.Id,"false",draft:true,draftSchema:"{\"type\":\"boolean\"}"));Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());using var value=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("Valid",value.RootElement.GetProperty("result").GetProperty("status").GetString());Assert.Equal(1,value.RootElement.GetProperty("evaluatedVersionRevision").GetInt64());
        await using var check=f.Context();Assert.Equal(before,(await check.Set<ApiSchema>().SingleAsync()).SchemaJson);Assert.Equal(1,(await check.Set<ApiVersion>().SingleAsync()).Revision);Assert.Equal(audits,await check.Set<AuditLog>().CountAsync());Assert.Equal(rows,await check.Set<IdempotencyRecord>().CountAsync());Assert.Empty(await check.Set<ApiVersionContractSources>().ToArrayAsync());
    }
    [Theory][InlineData("0","Valid")][InlineData("null","Invalid")][InlineData("\"PRIVATE-EXAMPLE\"","Invalid")]
    public async Task RealJsonExamplesAreValidatedWithoutEcho(string example,string expected)
    {var s=await Setup();await using var f=s.Fixture;using var r=await f.WriteAsync(HttpMethod.Post,Path(f),Body(s.Schema.Id,example));Assert.True(r.IsSuccessStatusCode,await r.Content.ReadAsStringAsync());var text=await r.Content.ReadAsStringAsync();using var value=JsonDocument.Parse(text);Assert.Equal(expected,value.RootElement.GetProperty("result").GetProperty("status").GetString());Assert.DoesNotContain("PRIVATE-EXAMPLE",text);Assert.NotEmpty(value.RootElement.GetProperty("result").GetProperty("exampleHash").GetString()!);}
    [Fact] public async Task ParameterSelectorUsesServerDefinition()
    {var s=await Setup();await using var f=s.Fixture;using var r=await f.WriteAsync(HttpMethod.Post,Path(f),Body(null,"\"x\"",parameterId:s.Parameter.Id));r.EnsureSuccessStatusCode();using var v=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.Equal("Invalid",v.RootElement.GetProperty("result").GetProperty("status").GetString());}
    [Theory][InlineData("permission")][InlineData("scope")][InlineData("revision")][InlineData("foreign")][InlineData("ambiguous")][InlineData("no_selector")][InlineData("draft_missing")][InlineData("draft_flag")][InlineData("invalid_json")]
    public async Task InvalidSelectionOrAuthorizationIsRejected(string change)
    {
        var s=await Setup();await using var f=s.Fixture;Guid? selector=s.Schema.Id;Guid? parameter=null;long revision=1;bool draft=false;string? draftSchema=null;var example="0";
        await using(var db=f.Context()){
            if(change=="permission")await db.Set<RolePermission>().Where(x=>db.Set<Permission>().Where(p=>p.Code=="api.schema.read").Select(p=>p.Id).Contains(x.PermissionId)).ExecuteDeleteAsync();
            if(change=="scope")await db.Set<UserProjectScope>().ExecuteDeleteAsync();
            if(change=="foreign"){var other=new ApiVersion{ApiId=f.Api.Id,Version="2",CreatedBy=f.User.Id};var foreign=new ApiSchema{ApiVersionId=other.Id,SchemaType="component",Name="Other",ContentType="application/json",SchemaJson="true"};db.AddRange(other,foreign);await db.SaveChangesAsync();selector=foreign.Id;}
        }
        if(change=="revision")revision=0;if(change=="ambiguous")parameter=s.Parameter.Id;if(change=="no_selector")selector=null;if(change=="draft_missing"){selector=null;draft=true;}if(change=="draft_flag")draftSchema="true";if(change=="invalid_json")example="<xml/>";
        using var r=await f.WriteAsync(HttpMethod.Post,Path(f),Body(selector,example,revision,draft,draftSchema,parameter));Assert.Equal(change is "permission" or "scope"?HttpStatusCode.NotFound:change=="revision"?HttpStatusCode.PreconditionFailed:HttpStatusCode.UnprocessableEntity,r.StatusCode);
    }
    [Fact] public async Task ClientCannotReplaceServerSourceBundle()
    {var s=await Setup();await using var f=s.Fixture;using var r=await f.WriteAsync(HttpMethod.Post,Path(f),new{expectedVersionRevision=1,schemaId=s.Schema.Id,exampleJson="0",direction="request",bundle=new{rootUri="https://attacker.invalid/schema"}});Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);}
    [Fact] public async Task NewDraftDoesNotNeedSavedDefinition()
    {var s=await Setup();await using var f=s.Fixture;using var r=await f.WriteAsync(HttpMethod.Post,Path(f),Body(null,"null",draft:true,draftSchema:"{\"type\":\"null\"}"));r.EnsureSuccessStatusCode();using var value=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.Equal("Valid",value.RootElement.GetProperty("result").GetProperty("status").GetString());}
    [Fact] public async Task HistoricalInvalidSchemaRemainsReadableWithClearDiagnostic()
    {var s=await Setup("{\"type\":\"nonsense\"}");await using var f=s.Fixture;using var read=await f.Client.GetAsync($"/api/v1/versions/{f.Version.Id}/schemas");read.EnsureSuccessStatusCode();Assert.Contains("nonsense",await read.Content.ReadAsStringAsync());using var r=await f.WriteAsync(HttpMethod.Post,Path(f),Body(s.Schema.Id));r.EnsureSuccessStatusCode();using var v=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.Equal("Invalid",v.RootElement.GetProperty("result").GetProperty("status").GetString());}
    [Fact] public async Task NonJsonMediaIsExplicitlyIncomplete()
    {var s=await Setup(contentType:"application/xml");await using var f=s.Fixture;using var r=await f.WriteAsync(HttpMethod.Post,Path(f),Body(s.Schema.Id));r.EnsureSuccessStatusCode();using var v=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.Equal("Incomplete",v.RootElement.GetProperty("result").GetProperty("status").GetString());Assert.Contains("schema_non_json_media",await r.Content.ReadAsStringAsync());}
    [Fact] public async Task SealedVersionSupportsTemporaryReadOnlyValidation()
    {var s=await Setup();await using var f=s.Fixture;await using(var db=f.Context())await db.Set<ApiVersion>().ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Status,"Published").SetProperty(v=>v.SealedAt,DateTimeOffset.UtcNow));using var r=await f.WriteAsync(HttpMethod.Post,Path(f),Body(s.Schema.Id));r.EnsureSuccessStatusCode();}
    [Fact] public async Task ImportedExternalParameterKeepsRelativeSourceInTemporaryAndSavedDraft()
    {
        var setup=await Setup();await using var f=setup.Fixture;
        const string text="""
        {"openapi":"3.1.0","info":{"title":"External","version":"1"},"paths":{"/external":{"get":{"operationId":"readOne","parameters":[{"$ref":"parts/param.json"}],"responses":{"200":{"description":"ok"}}}},"/unselected":{"get":{"operationId":"other","responses":{"200":{"description":"ok"}}}}}}
        """;
        object[] files=[new{name="parts/param.json",content="{\"name\":\"filter\",\"in\":\"query\",\"schema\":{\"$ref\":\"schemas/filter.json\"}}",format="json"},new{name="parts/schemas/filter.json",content="{\"type\":\"integer\"}",format="json"}];
        var preview=await ImportSessionTests.Preview(f,ImportSessionTests.Input(f,text,files));using var commit=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{preview.PreviewId}/commit",ImportSessionTests.Commit(preview));commit.EnsureSuccessStatusCode();Guid versionId;ApiParameter parameter;string bundle;
        await using(var db=f.Context()){var row=await db.Set<ApiVersionContractSources>().SingleAsync();versionId=row.ApiVersionId;bundle=row.BundleJson;parameter=await db.Set<ApiParameter>().SingleAsync(x=>x.ApiVersionId==versionId);var v=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==versionId);Assert.DoesNotContain("unselected",v.OpenapiDocument!);Assert.Contains("unselected",bundle);}
        using var validation=await f.WriteAsync(HttpMethod.Post,$"/api/v1/versions/{versionId}/schema-validation",Body(null,parameterId:parameter.Id));Assert.True(validation.IsSuccessStatusCode,await validation.Content.ReadAsStringAsync());Assert.True((await validation.Content.ReadAsStringAsync()).Contains("\"status\":\"Valid\"",StringComparison.Ordinal),await validation.Content.ReadAsStringAsync());
        using var draft=await f.WriteAsync(HttpMethod.Post,$"/api/v1/versions/{versionId}/schema-validation",Body(null,"0",draft:true,draftSchema:"{\"allOf\":[{\"$ref\":\"schemas/filter.json\"},{\"minimum\":2}]}",parameterId:parameter.Id));draft.EnsureSuccessStatusCode();Assert.Contains("\"status\":\"Invalid\"",await draft.Content.ReadAsStringAsync());
        using var saved=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{versionId}/parameters",new[]{new WebApi.Contracts.Catalog.SaveParameterRequest(parameter.Id,parameter.Location,parameter.Name,parameter.DataType,parameter.Required,"{\"allOf\":[{\"$ref\":\"schemas/filter.json\"},{\"minimum\":2}]}")},"\"1\"");Assert.True(saved.IsSuccessStatusCode,await saved.Content.ReadAsStringAsync());
        using var after=await f.WriteAsync(HttpMethod.Post,$"/api/v1/versions/{versionId}/schema-validation",Body(null,"0",2,parameterId:parameter.Id));after.EnsureSuccessStatusCode();Assert.Contains("\"status\":\"Invalid\"",await after.Content.ReadAsStringAsync());await using var check=f.Context();Assert.Equal(bundle,(await check.Set<ApiVersionContractSources>().SingleAsync()).BundleJson);
    }
    [Fact] public async Task ImportedEmbeddedIdKeepsPhysicalSourceAndLocation()
    {
        var setup=await Setup();await using var f=setup.Fixture;var text=ImportAndCredentialTests.Source.Replace("\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}}","\"$id\":\"embedded/order\",\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}}");var preview=await ImportSessionTests.Preview(f,ImportSessionTests.Input(f,text));using var commit=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{preview.PreviewId}/commit",ImportSessionTests.Commit(preview));commit.EnsureSuccessStatusCode();Guid versionId;Guid schemaId;await using(var db=f.Context()){versionId=(await db.Set<ApiVersionContractSources>().SingleAsync()).ApiVersionId;schemaId=(await db.Set<ApiSchema>().SingleAsync(x=>x.ApiVersionId==versionId&&x.SchemaType=="component")).Id;}
        using var response=await f.WriteAsync(HttpMethod.Post,$"/api/v1/versions/{versionId}/schema-validation",Body(schemaId,"{\"id\":\"bad\"}"));Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());using var value=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var result=value.RootElement.GetProperty("result");Assert.Equal("Invalid",result.GetProperty("status").GetString());Assert.Contains(result.GetProperty("issues").EnumerateArray(),x=>x.GetProperty("keyword").GetString()=="type"&&x.GetProperty("line").ValueKind==JsonValueKind.Number);
    }

    [Fact] public async Task ServerSelectorPreservesPercentSlashAndTildeInDefinitionName()
    {
        var setup=await Setup();await using var f=setup.Fixture;await using(var db=f.Context())await db.Set<ApiSchema>().ExecuteUpdateAsync(x=>x.SetProperty(s=>s.Name,"Part%2F/~name"));
        using var response=await f.WriteAsync(HttpMethod.Post,Path(f),Body(setup.Schema.Id));Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());var body=await response.Content.ReadAsStringAsync();Assert.True(body.Contains("\"status\":\"Valid\"",StringComparison.Ordinal),body);
    }

}

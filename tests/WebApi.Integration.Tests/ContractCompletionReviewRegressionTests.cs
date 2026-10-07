using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ContractCompletionReviewRegressionTests
{
    private static async Task<Guid> Import(ApiFixture f,string text,string code,object[]? files=null,Guid? apiId=null)
    {
        var preview=await ImportSessionTests.Preview(f,ImportSessionTests.Input(f,text,files));Assert.All(preview.Operations,x=>Assert.True(x.Supported));
        var targets=apiId is null?new object[]{new{operationId="readOne",newApiCode=code,newApiName=code,version="1.0.0"}}:[new{operationId="readOne",apiId,version="2.0.0"}];
        using var response=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{preview.PreviewId}/commit",ImportSessionTests.Commit(preview,targets:targets));Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return body.RootElement.GetProperty("operations")[0].GetProperty("versionId").GetGuid();
    }
    private static string Root(string responses,string version="3.1.0",string schema="")=>"{\"openapi\":\""+version+"\",\"info\":{\"title\":\"Review\",\"version\":\"1\"},\"paths\":{\"/review\":{\"get\":{\"operationId\":\"readOne\",\"responses\":"+responses+"}}}"+schema+"}";
    private const string Response="{\"description\":\"ok\",\"content\":{\"application/json\":{\"schema\":{\"type\":\"string\"}}}}";
    [Theory][InlineData("200",null)][InlineData("2XX",200)]
    public async Task EditingImportedResponseScopeCannotHideConflictWithFixedSource(string selector,int? status)
    {
        var setup=await SchemaValidationApiTests.Setup();await using var f=setup.Fixture;
        var text=Root("{\""+selector+"\":"+Response+"}");var from=await Import(f,text,"EDIT_SCOPE");
        Guid api;await using(var db=f.Context()){api=(await db.Set<ApiVersion>().SingleAsync(x=>x.Id==from)).ApiId;var routes=await db.Set<ApiRoute>().Where(x=>x.ApiVersionId==from).ToArrayAsync();var ids=routes.Select(x=>x.Id).ToArray();db.RemoveRange(await db.Set<RoutePolicyBinding>().Where(x=>ids.Contains(x.RouteId)).ToArrayAsync());db.RemoveRange(await db.Set<RouteMethod>().Where(x=>ids.Contains(x.RouteId)).ToArrayAsync());db.RemoveRange(routes);await db.SaveChangesAsync();}
        var id=await Import(f,text,"EDIT_SCOPE2",apiId:api);
        ApiSchema[] definitions;await using(var db=f.Context())definitions=await db.Set<ApiSchema>().Where(x=>x.ApiVersionId==id).ToArrayAsync();
        using var saved=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{id}/schemas",definitions.Select(x=>new SaveSchemaRequest(x.Id,x.SchemaType,x.Name,status,x.ContentType,x.SchemaJson)).ToArray(),"\"1\"");saved.EnsureSuccessStatusCode();
        using var compared=await f.WriteAsync(HttpMethod.Post,$"/api/v1/apis/{api}/version-comparisons",new{fromVersionId=from,toVersionId=id,expectedFromRevision=1,expectedToRevision=2});compared.EnsureSuccessStatusCode();
        using var body=JsonDocument.Parse(await compared.Content.ReadAsStringAsync());Assert.True(body.RootElement.GetProperty("report").GetProperty("counts").GetProperty("unknown").GetInt32()>0);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ImportedResponseSelectorsKeepRangesAndDefaultDistinct(bool withDefault)
    {
        var setup=await SchemaValidationApiTests.Setup();await using var f=setup.Fixture;
        var before=Root("{\"2XX\":"+Response+(withDefault?",\"default\":"+Response:"")+"}");var from=await Import(f,before,"RANGE");Guid api;await using(var db=f.Context())api=(await db.Set<ApiVersion>().SingleAsync(x=>x.Id==from)).ApiId;
        await using(var db=f.Context()){var routes=await db.Set<ApiRoute>().Where(x=>x.ApiVersionId==from).ToArrayAsync();var ids=routes.Select(x=>x.Id).ToArray();db.RemoveRange(await db.Set<RoutePolicyBinding>().Where(x=>ids.Contains(x.RouteId)).ToArrayAsync());db.RemoveRange(await db.Set<RouteMethod>().Where(x=>ids.Contains(x.RouteId)).ToArrayAsync());db.RemoveRange(routes);await db.SaveChangesAsync();}
        var after=withDefault?before:Root("{\"4XX\":"+Response+"}");var to=await Import(f,after,"RANGE2",apiId:api);
        using var compared=await f.WriteAsync(HttpMethod.Post,$"/api/v1/apis/{api}/version-comparisons",new{fromVersionId=from,toVersionId=to,expectedFromRevision=1,expectedToRevision=1});Assert.True(compared.IsSuccessStatusCode,await compared.Content.ReadAsStringAsync());
        using var body=JsonDocument.Parse(await compared.Content.ReadAsStringAsync());var report=body.RootElement.GetProperty("report");Assert.NotEqual("Invalid",report.GetProperty("coverage").GetString());
        if(!withDefault)Assert.True(report.GetProperty("counts").GetProperty("breaking").GetInt32()+report.GetProperty("counts").GetProperty("unknown").GetInt32()>0);
        else Assert.Equal(0,report.GetProperty("counts").GetProperty("unknown").GetInt32());
    }
    [Theory][InlineData("new")][InlineData("modified")][InlineData("unchanged")]
    public async Task UnknownKeywordsSurviveWholeDefinitionSaveButRemainSemanticallyIncomplete(string mode)
    {
        const string raw="{\"type\":\"integer\",\"x-maintenance-note\":\"keep\"}";
        var setup=await SchemaValidationApiTests.Setup(mode=="unchanged"?raw:"{\"type\":\"integer\"}");await using var f=setup.Fixture;
        var values=new List<SaveSchemaRequest>{new(setup.Schema.Id,"component","Value",null,"application/json",mode=="new"?setup.Schema.SchemaJson:raw)};
        if(mode is "new" or "unchanged")values.Add(new(null,"component","Other",null,"application/json",mode=="new"?raw:"{\"type\":\"string\"}"));
        using var response=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/schemas",values,"\"1\"");Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        Guid id;await using(var db=f.Context()){var saved=await db.Set<ApiSchema>().SingleAsync(x=>x.Name==(mode=="new"?"Other":"Value"));Assert.Contains("x-maintenance-note",saved.SchemaJson);Assert.Contains("keep",saved.SchemaJson);id=saved.Id;}
        using var validation=await f.WriteAsync(HttpMethod.Post,SchemaValidationApiTests.Path(f),SchemaValidationApiTests.Body(id,"0",2));validation.EnsureSuccessStatusCode();Assert.Contains("\"status\":\"Incomplete\"",await validation.Content.ReadAsStringAsync());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task MetadataOnlyEditPreservesFixedExternalPackageAndClippedOperation(bool fullEditor)
    {
        var setup=await SchemaValidationApiTests.Setup();await using var f=setup.Fixture;
        var text=Root("{\"200\":{\"description\":\"ok\",\"content\":{\"application/json\":{\"schema\":{\"$ref\":\"schema.json\"}}}}}");
        var root=JsonNode.Parse(text)!;root["paths"]!["/other"]=JsonNode.Parse("{\"get\":{\"operationId\":\"other\",\"responses\":{\"200\":{\"description\":\"ok\"}}}}");
        var versionId=await Import(f,root.ToJsonString(),"METADATA",[new{name="schema.json",content="{\"type\":\"string\"}",format="json"}]);
        ApiVersion version;ApiVersionContractSources source;await using(var db=f.Context()){version=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==versionId);source=await db.Set<ApiVersionContractSources>().SingleAsync(x=>x.ApiVersionId==versionId);}
        object input=fullEditor?new{version="renamed",changeType="compatible",openapiDocument=version.OpenapiDocument,openapiSource=version.OpenapiSource,sourceFormat=version.SourceFormat}:new{version="renamed",changeType="compatible"};
        using var response=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{versionId}",input,"\"1\"");Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        await using var check=f.Context();var current=await check.Set<ApiVersionContractSources>().SingleAsync(x=>x.ApiVersionId==versionId);Assert.Equal(source.BundleJson,current.BundleJson);Assert.Equal(source.SourcesJson,current.SourcesJson);Assert.Equal(source.BundleHash,current.BundleHash);Assert.Equal(source.SourcePolicyRevision,current.SourcePolicyRevision);Assert.Equal(version.OpenapiSource,(await check.Set<ApiVersion>().SingleAsync(x=>x.Id==versionId)).OpenapiSource);
    }
    [Fact] public async Task Oas30ReferenceSiblingsAreIgnoredDuringPreviewCommitAndSchemaSave()
    {
        var setup=await SchemaValidationApiTests.Setup();await using var f=setup.Fixture;
        const string schema="{\"$ref\":\"#/components/schemas/Value\",\"properties\":{\"ignored\":{\"$ref\":\"missing.json\"}}}";
        var text=Root("{\"200\":{\"description\":\"ok\",\"content\":{\"application/json\":{\"schema\":"+schema+"}}}}","3.0.3",",\"components\":{\"schemas\":{\"Value\":{\"type\":\"string\"}}}");
        var id=await Import(f,text,"LEGACY_REF");ApiSchema[] definitions;await using(var db=f.Context())definitions=await db.Set<ApiSchema>().Where(x=>x.ApiVersionId==id).ToArrayAsync();
        using var saved=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{id}/schemas",definitions.Select(x=>new SaveSchemaRequest(x.Id,x.SchemaType,x.Name,x.StatusCode,x.ContentType,x.SchemaJson)).ToArray(),"\"1\"");Assert.True(saved.IsSuccessStatusCode,await saved.Content.ReadAsStringAsync());
    }
}

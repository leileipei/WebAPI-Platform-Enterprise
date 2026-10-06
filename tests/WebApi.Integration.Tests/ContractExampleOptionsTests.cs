using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ContractExampleOptionsTests
{
    [Fact]public void OversizedExternalExampleIsUnverifiedWithoutExpandingTheResponse()
    {
        var reader=new WebApi.Infrastructure.Contracts.ContractDocumentReader();var uri=new Uri("https://fixed.invalid/examples.json");using var root=JsonDocument.Parse("{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"Example\",\"version\":\"1\"},\"paths\":{}}");
        var node=System.Text.Json.Nodes.JsonNode.Parse(root.RootElement.GetRawText())!;node["paths"]=System.Text.Json.Nodes.JsonNode.Parse("{\"/example\":{\"get\":{\"responses\":{\"200\":{\"description\":\"ok\",\"content\":{\"application/json\":{\"schema\":{},\"examples\":{\"External\":{\"externalValue\":\"placeholder\"}}}}}}}}}");node["paths"]!["/example"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["examples"]!["External"]!["externalValue"]="https://external.invalid/"+new string('a',100_000);
        var document=reader.Read(new(uri,node.ToJsonString(),"json"),new(),default);var bundle=WebApi.Infrastructure.Contracts.ContractBundleCodec.Create(uri,[document]);var id=Guid.NewGuid();var metadata=new WebApi.Contracts.OpenApi.ContractSourceMetadata([new(uri,"json")],[new(id,"schema",uri,"/paths/~1example/get/responses/200/content/application~1json/schema","hash",uri)]);var projection=new WebApi.Infrastructure.Catalog.ContractExampleProjection(new(bundle,metadata,0),default);var options=projection.Read(id,"schema","{}");var option=Assert.Single(options!);Assert.Null(option.ExternalValue);Assert.NotNull(option.UnverifiedReason);
    }
    [Fact]public async Task ImportedMediaAndSchemaExamplesKeepFalsyValuesAndOfflineReferences()
    {
        var setup=await SchemaValidationApiTests.Setup();await using var f=setup.Fixture;
        const string source="""
        {"openapi":"3.1.0","info":{"title":"Examples","version":"1"},"paths":{"/example":{"post":{"operationId":"example","requestBody":{"content":{"application/json":{"schema":{"examples":[false,0,"",null]},"examples":{"False":{"value":false},"Zero":{"value":0},"Empty":{"value":""},"Null":{"value":null},"Offline":{"$ref":"#/components/examples/Reusable"},"Remote":{"externalValue":"https://external.invalid/example"}}}}},"responses":{"200":{"description":"ok"}}}}},"components":{"examples":{"Reusable":{"value":{"ok":true}}}}}
        """;
        var preview=await ImportSessionTests.Preview(f,ImportSessionTests.Input(f,source));using var commit=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{preview.PreviewId}/commit",ImportSessionTests.Commit(preview,targets:[new{operationId="example",newApiCode="EXAMPLES",newApiName="Examples",version="1.0.0"}]));Assert.True(commit.IsSuccessStatusCode,await commit.Content.ReadAsStringAsync());Guid versionId;
        await using(var db=f.Context())versionId=(await db.Set<ApiVersionContractSources>().SingleAsync()).ApiVersionId;
        using var response=await f.Client.GetAsync($"/api/v1/versions/{versionId}/schemas");response.EnsureSuccessStatusCode();var rows=await response.Content.ReadFromJsonAsync<JsonElement>();var request=Assert.Single(rows.EnumerateArray(),row=>row.GetProperty("schemaType").GetString()=="request");Assert.True(request.TryGetProperty("examples",out var options));
        var values=options.EnumerateArray().Where(x=>x.TryGetProperty("json",out _)).Select(x=>x.GetProperty("json").GetString()).ToArray();Assert.Contains("false",values);Assert.Contains("0",values);Assert.Contains("\"\"",values);Assert.Contains("null",values);Assert.Contains("{\"ok\":true}",values);Assert.Contains(options.EnumerateArray(),x=>x.TryGetProperty("externalValue",out var url)&&url.GetString()=="https://external.invalid/example");
        using var version=await f.Client.GetAsync($"/api/v1/versions/{versionId}");version.EnsureSuccessStatusCode();Assert.Equal(1,(await version.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64());
    }
}

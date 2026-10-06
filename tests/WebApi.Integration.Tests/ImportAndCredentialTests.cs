using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ImportAndCredentialTests
{
    internal const string Source="""
    {"openapi":"3.1.0","info":{"title":"Orders","version":"1"},"paths":{
      "/import/one":{"parameters":[{"name":"X-Trace","in":"header","schema":{"type":"string"}}],"get":{"operationId":"readOne","parameters":[{"name":"x-trace","in":"header","schema":{"type":"string"}},{"name":"q","in":"query","schema":{"type":"string"}},{"name":"Q","in":"query","schema":{"type":"integer"}}],"responses":{"200":{"description":"ok","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Order"}}}}}}},
      "/import/two":{"post":{"operationId":"writeTwo","requestBody":{"content":{"application/json":{"schema":{"$ref":"#/components/schemas/Order"},"example":{"id":1}}}},"responses":{"201":{"description":"created","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Order"}}}},"400":{"description":"bad","content":{"application/json":{"schema":{"type":"object"}}}}}}}
    },"components":{"schemas":{"Order":{"type":"object","properties":{"id":{"type":"integer"}}}}}}
    """;
    private static object Preview(ApiFixture api,string? source=null)=>new {projectId=api.Project.Id,environmentId=api.Environment.Id,clusterId=api.Cluster.Id,source=source??Source};
    private static object Target(string id,string code)=>new {operationId=id,newApiCode=code,newApiName=code,version="1.0.0"};
    private static object Commit(ApiFixture api,object[] targets,string? source=null)=>new {input=Preview(api,source),targets};
    [Fact] public async Task BatchImportIsAtomic()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var result=await api.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-commit",Commit(api,[Target("readOne","NEW1"),Target("writeTwo","bad code")]));Assert.Equal(HttpStatusCode.UnprocessableEntity,result.StatusCode);
        await using var db=api.Context();Assert.Equal(1,await db.Set<Api>().CountAsync());Assert.Equal(1,await db.Set<ApiVersion>().CountAsync());Assert.Empty(await db.Set<ApiRoute>().ToListAsync());Assert.Empty(await db.Set<ApiParameter>().ToListAsync());Assert.False(await db.Set<AuditLog>().AnyAsync(x=>x.Action=="openapi.import"));
    }
    [Fact] public async Task PreviewDoesNotAuthorizeCommit()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var preview=await api.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-preview",Preview(api));Assert.Equal(HttpStatusCode.OK,preview.StatusCode);
        await using(var db=api.Context()) {await db.Set<UserProjectScope>().ExecuteDeleteAsync();}
        using var commit=await api.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-commit",Commit(api,[Target("readOne","NEW1")]));Assert.Equal(HttpStatusCode.Forbidden,commit.StatusCode);
        await using var check=api.Context();Assert.Equal(1,await check.Set<Api>().CountAsync());
    }
    [Fact] public async Task HeaderNamesCaseInsensitiveQueryNamesCaseSensitive()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var commit=await api.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-commit",Commit(api,[Target("readOne","IMPORTED")]));Assert.Equal(HttpStatusCode.OK,commit.StatusCode);
        await using var db=api.Context();var parameters=await db.Set<ApiParameter>().ToArrayAsync();Assert.Equal(1,parameters.Count(p=>p.Location=="header"));Assert.Contains(parameters,p=>p.Name=="q");Assert.Contains(parameters,p=>p.Name=="Q");Assert.Equal(3,parameters.Length);
        Assert.Equal(Source,(await db.Set<ApiVersion>().SingleAsync(v=>v.ApiId!=api.Api.Id)).OpenapiSource);
    }
    [Fact] public async Task LocalReferencesBodyAndMultipleResponsesPersist()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        using var commit=await api.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-commit",Commit(api,[Target("writeTwo","IMPORTED")]));Assert.Equal(HttpStatusCode.OK,commit.StatusCode);
        await using var db=api.Context();var schemas=await db.Set<ApiSchema>().ToArrayAsync();Assert.Contains(schemas,s=>s.SchemaType=="request"&&s.ExampleJson!.Contains("1"));Assert.Contains(schemas,s=>s.StatusCode==201);Assert.Contains(schemas,s=>s.StatusCode==400);Assert.Contains(schemas,s=>s.SchemaType=="component"&&s.Name=="Order");var source=await db.Set<ApiVersionContractSources>().SingleAsync();var bundle=JsonSerializer.Deserialize<ContractBundle>(source.BundleJson,WebApi.Contracts.Common.CanonicalJson.Options)!;var registry=new ContractReferenceRegistry(bundle,new());
        var requestSchema=JsonNode.Parse(schemas.Single(x=>x.SchemaType=="request").SchemaJson)!;Assert.Equal("#/components/schemas/Order",requestSchema["$ref"]!.GetValue<string>());var resolved=registry.Resolve(bundle.RootUri,requestSchema["$ref"]!.GetValue<string>());Assert.Equal("integer",resolved.Node["properties"]!["id"]!["type"]!.GetValue<string>());
    }
    [Theory] [InlineData("https://untrusted.example/schema")] [InlineData("#/components/schemas/Missing")]
    public async Task MissingExternalOrLocalReferencesAreExplicitlyRejected(string reference)
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        var source=Source.Replace("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}}}",JsonSerializer.Serialize(new Dictionary<string,string>{{"$ref",reference}}));
        using var commit=await api.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-commit",Commit(api,[Target("readOne","IMPORTED")],source));Assert.Equal(HttpStatusCode.UnprocessableEntity,commit.StatusCode);
        var text=await commit.Content.ReadAsStringAsync();Assert.Contains("missing_contract_reference",text);
    }
    [Fact] public async Task ValidRecursiveComponentPersistsWithoutExpansion()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();var source=Source.Replace("\"id\":{\"type\":\"integer\"}","\"id\":{\"type\":\"integer\"},\"child\":{\"$ref\":\"#/components/schemas/Order\"}");
        using var r=await api.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-commit",Commit(api,[Target("writeTwo","RECURSIVE")],source));r.EnsureSuccessStatusCode();await using var db=api.Context();var schema=await db.Set<ApiSchema>().SingleAsync(x=>x.SchemaType=="component");Assert.Contains("$ref",schema.SchemaJson);Assert.True(schema.SchemaJson.Length<1000);
    }
    [Fact] public async Task SecretOnlyReturnedOnce()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();using var login=await api.LoginAsync();var start=DateTimeOffset.UtcNow;
        using var create=await api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{api.Application.Id}/credentials",new {validFrom=start,expiresAt=start.AddDays(30)});Assert.Equal(HttpStatusCode.OK,create.StatusCode);
        var body=JsonDocument.Parse(await create.Content.ReadAsStringAsync());var secret=body.RootElement.GetProperty("secret").GetString()!;Assert.Equal(32,Convert.FromBase64String(secret.Replace('-','+').Replace('_','/')+"=").Length);
        using var list=await api.Client.GetAsync($"/api/v1/applications/{api.Application.Id}/credentials");Assert.Equal(HttpStatusCode.OK,list.StatusCode);var text=await list.Content.ReadAsStringAsync();Assert.DoesNotContain(secret,text);Assert.DoesNotContain("secretHash",text,StringComparison.OrdinalIgnoreCase);
        await using var db=api.Context();var credential=await db.Set<ApplicationCredential>().SingleAsync();Assert.NotEqual(secret,credential.SecretHash);Assert.Equal(64,credential.SecretHash.Length);Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret))),credential.SecretHash);Assert.True(WebApi.Infrastructure.Security.ApiKeySecret.Verify(credential.SecretHash,secret));Assert.False(WebApi.Infrastructure.Security.ApiKeySecret.Verify(credential.SecretHash,"wrong"));Assert.DoesNotContain(secret,string.Join("\n",(await db.Set<AuditLog>().ToArrayAsync()).Select(a=>a.BeforeJson+a.AfterJson+a.Action+a.ResourceId+a.TraceId)));
    }
    [Fact] public async Task CredentialWindowRejectsInvalidRange()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();using var login=await api.LoginAsync();var start=DateTimeOffset.UtcNow;
        using var create=await api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{api.Application.Id}/credentials",new {validFrom=start,expiresAt=start});Assert.Equal(HttpStatusCode.UnprocessableEntity,create.StatusCode);
        await using var db=api.Context();Assert.Empty(await db.Set<ApplicationCredential>().ToListAsync());
    }
    [Fact] public async Task LocalPathItemReferenceCanBeImported()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedCatalogAsync();using var login=await api.LoginAsync();
        const string source="""
        {"openapi":"3.1.0","info":{"title":"Reference","version":"1"},"paths":{"/path-reference":{"$ref":"#/components/pathItems/Reference"}},"components":{"pathItems":{"Reference":{"get":{"operationId":"referencedOperation","responses":{"204":{"description":"no content"}}}}}}}
        """;
        using var result=await api.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-commit",Commit(api,[Target("referencedOperation","PATHREF")],source));Assert.Equal(HttpStatusCode.OK,result.StatusCode);
        await using var db=api.Context();Assert.Equal("/path-reference",(await db.Set<ApiRoute>().SingleAsync()).Path);
    }
}

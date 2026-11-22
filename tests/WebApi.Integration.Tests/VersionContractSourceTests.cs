using WebApi.Integration.Tests.Support;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class VersionContractSourceTests(MigratedDatabaseTemplateFixture template) : IClassFixture<MigratedDatabaseTemplateFixture>
{
    private const string Yaml="openapi: 3.1.0\ninfo: {title: Test, version: '1'}\npaths: {}\ncomponents:\n  schemas:\n    Value: {type: integer}\n";

    private sealed class VersionReads : DbCommandInterceptor
    {
        public List<string> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("FROM api_versions", StringComparison.Ordinal)) Queries.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private async Task<ApiFixture> ContractReaderAsync(string? localRead, string mode, VersionReads? reads = null)
    {
        return await ApiFixture.CreateAsync(async f =>
        {
            await f.InitializeAsync(builder =>
            {
                if (reads is not null) builder.Services.AddDbContext<WebApiDbContext>(options => options.AddInterceptors(reads));
            });
            await f.SeedCatalogAsync();
            await using var db = f.Context();
            var version = await db.Set<ApiVersion>().SingleAsync();
            version.OpenapiDocument = SchemaValidationApiTests.Root;
            version.OpenapiSource = Yaml;
            version.SourceFormat = "yaml";
            var localRole = await db.Set<UserRole>().Where(r => r.UserId == f.User.Id).Select(r => r.RoleId).SingleAsync();
            var other = new Organization { Code = "OTHER", Name = "其他组织" };
            var role = new Role { OrganizationId = other.Id, Code = "Contracts", Name = "契约读取" };
            db.AddRange(other, role, new UserRole { UserId = f.User.Id, RoleId = role.Id },
                new UserProjectScope { UserId = f.User.Id, OrganizationId = other.Id, AccessMode = "read_write" });
            var scope = await db.Set<UserProjectScope>().SingleAsync(s => s.UserId == f.User.Id);
            scope.ProjectId = f.Project.Id;
            scope.AccessMode = mode;
            foreach (var grant in await db.Set<Permission>().Where(p => p.Code == "api.version.read" || p.Code == "api.schema.read").ToArrayAsync())
            {
                db.Add(new RolePermission { RoleId = role.Id, PermissionId = grant.Id });
                if (grant.Code != localRead)
                    await db.Set<RolePermission>().Where(r => r.RoleId == localRole && r.PermissionId == grant.Id).ExecuteDeleteAsync();
            }
            await db.SaveChangesAsync();
            using var login = await f.LoginAsync();
            login.EnsureSuccessStatusCode();
        }, template.Template);
    }

    private static void AssertContract(VersionDto version, string? source)
    {
        Assert.Equal(source, version.OpenapiSource);
        if (source is null) Assert.Null(version.OpenapiDocument);
        else
        {
            using var document = JsonDocument.Parse(version.OpenapiDocument!);
            Assert.Equal("3.1.0", document.RootElement.GetProperty("openapi").GetString());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("api.version.read")]
    [InlineData("api.schema.read")]
    public async Task CatalogAndOpenApiRepresentationsRespectContractReadInTheCurrentProject(string? localRead)
    {
        var reads = new VersionReads();
        await using var f = await ContractReaderAsync(localRead, "read_only", reads);
        var apiPath = $"/api/v1/apis/{f.Api.Id}";
        var versionPath = $"/api/v1/versions/{f.Version.Id}";
        var detail = await f.Client.GetFromJsonAsync<ApiDetailDto>(apiPath);
        Assert.Equal(localRead == "api.schema.read", detail!.CanReadSchemas);
        Assert.Equal(localRead == "api.version.read", detail.CanReadVersions);
        var version = Assert.Single(detail.Versions);
        Assert.Equal(f.Version.Id, version.Id);
        Assert.Equal(1, version.Revision);
        Assert.Equal("yaml", version.SourceFormat);
        AssertContract(version, localRead is null ? null : Yaml);

        // The version resource and collection retain their existing version-read requirement.
        using var resource = await f.Client.GetAsync(versionPath);
        using var collection = await f.Client.GetAsync(apiPath + "/versions");
        var versionStatus = localRead == "api.version.read" ? HttpStatusCode.OK : HttpStatusCode.NotFound;
        Assert.Equal(versionStatus, resource.StatusCode);
        Assert.Equal(versionStatus, collection.StatusCode);
        if (resource.IsSuccessStatusCode)
        {
            AssertContract((await resource.Content.ReadFromJsonAsync<VersionDto>())!, Yaml);
            AssertContract(Assert.Single((await collection.Content.ReadFromJsonAsync<VersionDto[]>())!), Yaml);
        }

        foreach (var representation in new[] { "source", "json" })
        {
            using var download = await f.Client.GetAsync($"{versionPath}/openapi?representation={representation}");
            Assert.Equal(localRead is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, download.StatusCode);
            if (!download.IsSuccessStatusCode) continue;
            if (representation == "source") Assert.Equal(Yaml, await download.Content.ReadAsStringAsync());
            else
            {
                var root = await download.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("integer", root.GetProperty("components").GetProperty("schemas").GetProperty("Value").GetProperty("type").GetString());
            }
        }
        Assert.NotEmpty(reads.Queries);
        if (localRead is null) Assert.All(reads.Queries, sql =>
        {
            Assert.DoesNotContain("openapi_source", sql);
            Assert.DoesNotContain("openapi_document", sql);
        });
    }

    [Fact]
    public async Task AnEnvironmentGrantDoesNotGiveAccessToTheProjectContract()
    {
        await using var f = await ContractReaderAsync("api.schema.read", "read_only");
        await using var db = f.Context();
        await db.Set<UserProjectScope>().Where(s => s.UserId == f.User.Id && s.ProjectId == f.Project.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.EnvironmentId, f.Environment.Id));
        using var detail = await f.Client.GetAsync($"/api/v1/apis/{f.Api.Id}?environmentId={f.Environment.Id}");
        using var document = await f.Client.GetAsync($"/api/v1/versions/{f.Version.Id}/openapi");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("api.schema.read")]
    public async Task VersionWritesPreserveOrReplaceContractsAndOnlyReturnReadableContent(string? localRead)
    {
        await using var f = await ContractReaderAsync(localRead, "read_write");
        var versionPath = $"/api/v1/versions/{f.Version.Id}";
        // Older forms sent their default format even when both document inputs were empty.
        using var edit = await f.WriteAsync(HttpMethod.Put, versionPath, new CreateVersionRequest("1.1", SourceFormat: "json"), "\"1\"");
        edit.EnsureSuccessStatusCode();
        var updated = (await edit.Content.ReadFromJsonAsync<VersionDto>())!;
        Assert.Equal("yaml", updated.SourceFormat);
        AssertContract(updated, localRead is null ? null : Yaml);
        await using var db = f.Context();
        Assert.Equal(Yaml, await db.Set<ApiVersion>().Where(v => v.Id == f.Version.Id).Select(v => v.OpenapiSource).SingleAsync());

        var replacement = Yaml.Replace("integer", "string", StringComparison.Ordinal);
        var request = new CreateVersionRequest("2.0", OpenapiSource: replacement, SourceFormat: "yaml");
        using var update = await f.WriteAsync(HttpMethod.Put, versionPath, request, edit.Headers.ETag!.Tag);
        using var create = await f.WriteAsync(HttpMethod.Post, $"/api/v1/apis/{f.Api.Id}/versions", request with { Version = "3.0" });
        foreach (var response in new[] { update, create })
        {
            response.EnsureSuccessStatusCode();
            var saved = (await response.Content.ReadFromJsonAsync<VersionDto>())!;
            AssertContract(saved, localRead is null ? null : replacement);
            var stored = await db.Set<ApiVersion>().AsNoTracking().SingleAsync(v => v.Id == saved.Id);
            Assert.Equal(replacement, stored.OpenapiSource);
            using var document = JsonDocument.Parse(stored.OpenapiDocument!);
            Assert.Equal("string", document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("Value").GetProperty("type").GetString());
        }
    }
    [Fact] public async Task YamlSourceDownloadHasCorrectMimeAndJsonRepresentation()
    {
        var s=await SchemaValidationApiTests.Setup(databaseTemplate:template.Template);await using var f=s.Fixture;
        using var saved=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}",new{version="1.0.1",openapiDocument=SchemaValidationApiTests.Root,openapiSource=Yaml,sourceFormat="yaml"},"\"1\"");Assert.True(saved.IsSuccessStatusCode,await saved.Content.ReadAsStringAsync());
        using var raw=await f.Client.GetAsync($"/api/v1/versions/{f.Version.Id}/openapi");raw.EnsureSuccessStatusCode();Assert.Equal("application/yaml",raw.Content.Headers.ContentType!.MediaType);Assert.Equal(Yaml,await raw.Content.ReadAsStringAsync());
        using var json=await f.Client.GetAsync($"/api/v1/versions/{f.Version.Id}/openapi?representation=json");json.EnsureSuccessStatusCode();Assert.Equal("application/json",json.Content.Headers.ContentType!.MediaType);using var parsed=JsonDocument.Parse(await json.Content.ReadAsStringAsync());Assert.Equal("3.1.0",parsed.RootElement.GetProperty("openapi").GetString());
        await using var db=f.Context();var row=await db.Set<ApiVersionContractSources>().SingleAsync();Assert.Equal("Oas31",row.Dialect);Assert.Contains("yaml",row.SourcesJson);Assert.Equal(2,(await db.Set<ApiVersion>().SingleAsync()).Revision);
    }
    [Fact] public async Task InconsistentRawAndCanonicalVersionRollsBack()
    {var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}",new{version="changed",openapiDocument=SchemaValidationApiTests.Root,openapiSource=Yaml.Replace("integer","string"),sourceFormat="yaml"},"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);await using var db=f.Context();Assert.Equal(1,(await db.Set<ApiVersion>().SingleAsync()).Revision);Assert.Empty(await db.Set<ApiVersionContractSources>().ToArrayAsync());}
    [Fact] public async Task DirectRootReplacementInvalidatesOldExternalBundle()
    {
        await using var f=await ImportSessionTests.Setup(template.Template);var text=ImportAndCredentialTests.Source.Replace("#/components/schemas/Order","schemas/order.json");var p=await ImportSessionTests.Preview(f,ImportSessionTests.Input(f,text,[new{name="schemas/order.json",content="{\"type\":\"integer\"}",format="json"}]));using var committed=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",ImportSessionTests.Commit(p));committed.EnsureSuccessStatusCode();Guid id;string oldHash;await using(var db=f.Context()){var row=await db.Set<ApiVersionContractSources>().SingleAsync();id=row.ApiVersionId;oldHash=row.BundleHash;}
        using var saved=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{id}",new{version="updated",openapiDocument=SchemaValidationApiTests.Root,openapiSource=SchemaValidationApiTests.Root,sourceFormat="json"},"\"1\"");saved.EnsureSuccessStatusCode();await using var read=f.Context();var current=await read.Set<ApiVersionContractSources>().SingleAsync();Assert.NotEqual(oldHash,current.BundleHash);Assert.DoesNotContain("schemas/order.json",current.BundleJson);Assert.Equal(2,(await read.Set<ApiVersion>().SingleAsync(x=>x.Id==id)).Revision);
    }
    [Theory][InlineData("sealed")][InlineData("revision")]
    public async Task RootReplacementHonorsRevisionAndSealing(string change)
    {var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;if(change=="sealed"){await using var db=f.Context();await db.Set<ApiVersion>().ExecuteUpdateAsync(x=>x.SetProperty(v=>v.SealedAt,DateTimeOffset.UtcNow));}using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}",new{version="changed",openapiSource=Yaml,sourceFormat="yaml"},change=="revision"?"\"0\"":"\"1\"");Assert.Equal(change=="sealed"?HttpStatusCode.Conflict:HttpStatusCode.PreconditionFailed,r.StatusCode);await using var check=f.Context();Assert.Empty(await check.Set<ApiVersionContractSources>().ToArrayAsync());}
    [Theory][InlineData("{\"type\":\"nonsense\"}")][InlineData("{\"$ref\":\"missing.json\"}")][InlineData("{\"required\":[1]}")]
    public async Task NewDamagedSchemaCannotSave(string schema)
    {var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;string before;await using(var initial=f.Context())before=(await initial.Set<ApiSchema>().SingleAsync()).SchemaJson;using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/schemas",new[]{new SaveSchemaRequest(s.Schema.Id,"component","Value",null,"application/json",schema)},"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);await using var db=f.Context();Assert.Equal(1,(await db.Set<ApiVersion>().SingleAsync()).Revision);Assert.Equal(before,(await db.Set<ApiSchema>().SingleAsync()).SchemaJson);}
    [Fact] public async Task NonMatchingExampleCanStillSaveDraft()
    {var s=await SchemaValidationApiTests.Setup(databaseTemplate:template.Template);await using var f=s.Fixture;using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/schemas",new[]{new SaveSchemaRequest(s.Schema.Id,"component","Value",null,"application/json","{\"type\":\"integer\",\"minimum\":5}","0")},"\"1\"");Assert.True(r.IsSuccessStatusCode,await r.Content.ReadAsStringAsync());await using var db=f.Context();Assert.Equal("0",(await db.Set<ApiSchema>().SingleAsync()).ExampleJson);Assert.Equal(2,(await db.Set<ApiVersion>().SingleAsync()).Revision);Assert.Single(await db.Set<ApiVersionContractSources>().ToArrayAsync());using var validation=await f.WriteAsync(HttpMethod.Post,SchemaValidationApiTests.Path(f),SchemaValidationApiTests.Body(s.Schema.Id,"0",2));validation.EnsureSuccessStatusCode();Assert.True((await validation.Content.ReadAsStringAsync()).Contains("\"status\":\"Invalid\"",StringComparison.Ordinal),await validation.Content.ReadAsStringAsync());}
    [Theory][InlineData("delete")][InlineData("rename")]
    public async Task ReferencedComponentChangeListsAffectedPointers(string change)
    {
        var s=await SchemaValidationApiTests.Setup(databaseTemplate:template.Template);await using var f=s.Fixture;var consumer=new ApiSchema{ApiVersionId=f.Version.Id,SchemaType="response",Name="Response",StatusCode=200,ContentType="application/json",SchemaJson="{\"$ref\":\"#/components/schemas/Value\"}"};await using(var db=f.Context()){db.Add(consumer);await db.SaveChangesAsync();}
        var candidates=new List<SaveSchemaRequest>{new(consumer.Id,"response","Response",200,"application/json",consumer.SchemaJson)};if(change=="rename")candidates.Add(new(s.Schema.Id,"component","Renamed",null,"application/json",s.Schema.SchemaJson));
        using var r=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/schemas",candidates,"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);using var problem=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.Equal("schema_reference_impact",problem.RootElement.GetProperty("code").GetString());Assert.Contains(problem.RootElement.GetProperty("issues").EnumerateArray(),x=>x.GetProperty("pointer").GetString()!.EndsWith("/$ref",StringComparison.Ordinal));await using var check=f.Context();Assert.Equal("Value",(await check.Set<ApiSchema>().SingleAsync(x=>x.Id==s.Schema.Id)).Name);Assert.Equal(1,(await check.Set<ApiVersion>().SingleAsync()).Revision);
    }
    [Fact] public async Task StoredBundleTamperingNeverReturnsValid()
    {
        var s=await SchemaValidationApiTests.Setup();await using var f=s.Fixture;using var save=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}",new{version="1",openapiSource=Yaml,sourceFormat="yaml"},"\"1\"");save.EnsureSuccessStatusCode();await using(var db=f.Context())await db.Set<ApiVersionContractSources>().ExecuteUpdateAsync(x=>x.SetProperty(v=>v.BundleHash,new string('0',64)));
        using var r=await f.WriteAsync(HttpMethod.Post,SchemaValidationApiTests.Path(f),SchemaValidationApiTests.Body(s.Schema.Id,revision:2));Assert.True(r.IsSuccessStatusCode,await r.Content.ReadAsStringAsync());Assert.Contains("\"status\":\"Incomplete\"",await r.Content.ReadAsStringAsync());
    }
    [Fact] public async Task JsonDownloadKeepsRawRepresentation()
    {var s=await SchemaValidationApiTests.Setup(databaseTemplate:template.Template);await using var f=s.Fixture;using var r=await f.Client.GetAsync($"/api/v1/versions/{f.Version.Id}/openapi");r.EnsureSuccessStatusCode();Assert.Equal("application/json",r.Content.Headers.ContentType!.MediaType);Assert.Equal(SchemaValidationApiTests.Root,await r.Content.ReadAsStringAsync());}
    [Fact] public async Task AmbiguousComponentNamesCannotOverwriteAnotherDefinition()
    {
        var setup=await SchemaValidationApiTests.Setup();await using var f=setup.Fixture;
        var values=new[]{new SaveSchemaRequest(setup.Schema.Id,"component","Value",null,"application/json","{\"type\":\"integer\"}"),new SaveSchemaRequest(null,"component","Value",null,"application/problem+json","{\"type\":\"boolean\"}")};
        using var response=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/schemas",values,"\"1\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.Contains("ambiguous_component_definition",await response.Content.ReadAsStringAsync());await using var db=f.Context();Assert.Single(await db.Set<ApiSchema>().ToArrayAsync());Assert.Equal(1,(await db.Set<ApiVersion>().SingleAsync()).Revision);
    }

}

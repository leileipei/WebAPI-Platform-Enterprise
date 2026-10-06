using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Contracts;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ImportSessionTests
{
    internal static object Input(ApiFixture f,string? text=null,object[]? files=null)=>new {projectId=f.Project.Id,environmentId=f.Environment.Id,clusterId=f.Cluster.Id,sourceText=text??ImportAndCredentialTests.Source,format="json",files};
    internal static async Task<ImportSessionDto> Preview(ApiFixture f,object? input=null)
    {
        using var r=await f.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-previews",input??Input(f));Assert.True(r.IsSuccessStatusCode,await r.Content.ReadAsStringAsync());return(await r.Content.ReadFromJsonAsync<ImportSessionDto>())!;
    }
    internal static object Commit(ImportSessionDto p,string code="SESSION",object[]? targets=null)=>new {expectedBundleHash=p.BundleHash,targets=targets??[new {operationId="readOne",newApiCode=code,newApiName=code,version="1.0.0"}]};
    internal static async Task<ApiFixture> Setup()
    {
        var f=new ApiFixture();await f.InitializeAsync();await f.SeedCatalogAsync();using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();return f;
    }
    [Fact] public async Task TextAndYamlPreviewPersistSourcesAndDraftRoutes()
    {
        await using var f=await Setup();var text="openapi: 3.1.0\ninfo: {title: Test, version: '1'}\npaths:\n  /yaml-session:\n    get:\n      operationId: readOne\n      responses: {'200': {description: ok}}\n";
        var p=await Preview(f,new {projectId=f.Project.Id,environmentId=f.Environment.Id,clusterId=f.Cluster.Id,sourceText=text,format="yaml"});Assert.Single(p.Operations);Assert.Equal(0,p.SourcePolicyRevision);using(var get=await f.Client.GetAsync($"/api/v1/openapi/import-previews/{p.PreviewId}")){get.EnsureSuccessStatusCode();using var value=System.Text.Json.JsonDocument.Parse(await get.Content.ReadAsStringAsync());Assert.Equal("yaml",value.RootElement.GetProperty("sourceFormat").GetString());}
        using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p));r.EnsureSuccessStatusCode();
        await using var db=f.Context();var version=await db.Set<ApiVersion>().SingleAsync(x=>x.ApiId!=f.Api.Id);Assert.Equal(text,version.OpenapiSource);Assert.Equal("yaml",version.SourceFormat);Assert.Equal("Draft",version.Status);Assert.Equal(p.BundleHash,(await db.Set<ApiVersionContractSources>().SingleAsync()).BundleHash);Assert.Equal("Committed",(await db.Set<ApiImportPreview>().SingleAsync()).Status);Assert.Single(await db.Set<RoutePolicyBinding>().ToArrayAsync());
    }
    [Theory][InlineData("hash")][InlineData("expired")][InlineData("policy")][InlineData("revoked")][InlineData("scope")][InlineData("cluster")]
    public async Task ChangedPreviewOrCurrentScopeCannotCommit(string change)
    {
        await using var f=await Setup();var p=await Preview(f);
        if(change=="revoked"){using var revoke=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/openapi/import-previews/{p.PreviewId}");revoke.EnsureSuccessStatusCode();}
        await using(var db=f.Context()){
            if(change=="expired")await db.Set<ApiImportPreview>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CreatedAt,DateTimeOffset.UtcNow.AddHours(-2)).SetProperty(x=>x.ExpiresAt,DateTimeOffset.UtcNow.AddHours(-1)));
            if(change=="policy")db.Add(new ProjectImportSourcePolicy{ProjectId=f.Project.Id,UpdatedBy=f.User.Id,UpdatedAt=DateTimeOffset.UtcNow,RulesJson=System.Text.Json.JsonSerializer.Serialize(new SaveImportSourcePolicyRequest([],new()),WebApi.Contracts.Common.CanonicalJson.Options)});
            if(change=="scope")await db.Set<UserProjectScope>().ExecuteDeleteAsync();if(change=="cluster")await db.Set<UpstreamCluster>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Inactive"));await db.SaveChangesAsync();}
        if(change=="hash")p=p with {BundleHash=new string('0',64)};
        using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p));Assert.False(r.IsSuccessStatusCode);Assert.Contains(r.StatusCode,new[]{HttpStatusCode.Conflict,HttpStatusCode.Forbidden,HttpStatusCode.NotFound,HttpStatusCode.UnprocessableEntity});await using var read=f.Context();Assert.Equal(1,await read.Set<Api>().CountAsync());Assert.Empty(await read.Set<ApiRoute>().ToArrayAsync());
    }
    [Fact] public async Task PreviewIsInvisibleToAnotherActor()
    {
        await using var f=await Setup();var p=await Preview(f);var foreign=await f.NewReviewerAsync("PreviewReader");using var get=await foreign.Client.GetAsync($"/api/v1/openapi/import-previews/{p.PreviewId}");Assert.Equal(HttpStatusCode.NotFound,get.StatusCode);using var commit=await ApiFixture.CommandAsync(foreign.Client,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p));Assert.Equal(HttpStatusCode.NotFound,commit.StatusCode);
    }
    [Fact] public async Task InvalidTargetRollsBackWholeBatchAndPreviewStatus()
    {
        await using var f=await Setup();var p=await Preview(f);object[] targets=[new {operationId="readOne",newApiCode="GOOD",newApiName="Good"},new {operationId="writeTwo",newApiCode="bad code",newApiName="Bad"}];using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p,targets:targets));Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);await using var db=f.Context();Assert.Equal(1,await db.Set<Api>().CountAsync());Assert.Empty(await db.Set<ApiVersionContractSources>().ToArrayAsync());Assert.Equal("Active",(await db.Set<ApiImportPreview>().SingleAsync()).Status);
    }
    [Fact] public async Task SameKeyReplayWorksAfterContentCleanupAndSealing()
    {
        await using var f=await Setup();var p=await Preview(f);var path=$"/api/v1/openapi/import-previews/{p.PreviewId}/commit";var key=Guid.NewGuid().ToString("N");var body=Commit(p);
        using var first=await ApiFixture.CommandAsync(f.Client,path,body,key);first.EnsureSuccessStatusCode();var bytes=await first.Content.ReadAsByteArrayAsync();
        await using(var db=f.Context()){await db.Set<ApiImportPreview>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CreatedAt,DateTimeOffset.UtcNow.AddHours(-2)).SetProperty(x=>x.ExpiresAt,DateTimeOffset.UtcNow.AddHours(-1)));await db.Set<ApiVersion>().Where(x=>x.ApiId!=f.Api.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Approved").SetProperty(x=>x.Revision,10));}
        using(var scope=f.Services())Assert.Equal(1,await scope.ServiceProvider.GetRequiredService<ImportPreviewCleanupService>().RunAsync(100,default));
        await using(var db=f.Context()){var row=await db.Set<ApiImportPreview>().SingleAsync();Assert.Null(row.BundleJson);Assert.Null(row.PreviewJson);Assert.NotNull(row.CommittedTargetsJson);}
        using var retry=await ApiFixture.CommandAsync(f.Client,path,body,key);retry.EnsureSuccessStatusCode();Assert.Equal(bytes,await retry.Content.ReadAsByteArrayAsync());using var different=await ApiFixture.CommandAsync(f.Client,path,body);Assert.Equal(HttpStatusCode.Conflict,different.StatusCode);
        await using(var db=f.Context())await db.Set<RolePermission>().Where(x=>db.Set<Permission>().Where(p=>p.Code=="api.create").Select(p=>p.Id).Contains(x.PermissionId)).ExecuteDeleteAsync();using var denied=await ApiFixture.CommandAsync(f.Client,path,body,key);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }
    [Theory][InlineData("../schema.json")][InlineData("/schema.json")][InlineData("schemas/../x.json")][InlineData("schemas\\x.json")][InlineData("https://example/schema.json")]
    public async Task UnsafeFileNamesAreRejected(string name)
    {
        await using var f=await Setup();using var r=await f.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-previews",Input(f,files:[new {name,content="true",format="json"}]));Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);await using var db=f.Context();Assert.Empty(await db.Set<ApiImportPreview>().ToArrayAsync());
    }
    [Fact] public async Task AttachedRecursiveSchemaIsKeptAsFiniteGraph()
    {
        await using var f=await Setup();var text=ImportAndCredentialTests.Source.Replace("#/components/schemas/Order","schemas/order.json");var p=await Preview(f,Input(f,text,[new {name="schemas/order.json",content="{\"$id\":\"order.json\",\"type\":\"object\",\"properties\":{\"child\":{\"$ref\":\"order.json\"}}}",format="json"}]));Assert.All(p.Operations,x=>Assert.True(x.Supported));using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p));r.EnsureSuccessStatusCode();await using var db=f.Context();Assert.Contains("schemas/order.json",(await db.Set<ApiVersionContractSources>().SingleAsync()).BundleJson);
    }

    [Fact] public async Task MidBatchRouteConflictLeavesNoPartialRows()
    {
        await using var f=await Setup();using var route=await f.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{f.Environment.Id}/routes",f.RouteBody("/import/two","POST"));route.EnsureSuccessStatusCode();var p=await Preview(f);
        object[] targets=[new{operationId="readOne",newApiCode="ROUTE1",newApiName="Route1"},new{operationId="writeTwo",newApiCode="ROUTE2",newApiName="Route2"}];using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p,targets:targets));Assert.Equal(HttpStatusCode.Conflict,r.StatusCode);await using var db=f.Context();Assert.Equal(1,await db.Set<Api>().CountAsync());Assert.Single(await db.Set<ApiRoute>().ToArrayAsync());Assert.Empty(await db.Set<ApiVersionContractSources>().ToArrayAsync());Assert.Equal("Active",(await db.Set<ApiImportPreview>().SingleAsync()).Status);
    }
    [Theory][InlineData("sealed")][InlineData("revision")][InlineData("foreign")][InlineData("permission")]
    public async Task ExistingVersionIsCheckedAgainAtCommit(string change)
    {
        await using var f=await Setup();var p=await Preview(f);Guid versionId=f.Version.Id;
        await using(var db=f.Context()){
            if(change=="sealed")await db.Set<ApiVersion>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.SealedAt,DateTimeOffset.UtcNow));
            if(change=="revision")await db.Set<ApiVersion>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Revision,2));
            if(change=="foreign"){var project=new Project{OrganizationId=f.Organization.Id,Code="FOREIGN",Name="Foreign"};var api=new Api{OrganizationId=f.Organization.Id,ProjectId=project.Id,Code="OTHER",Name="Other",OwnerUserId=f.User.Id};var version=new ApiVersion{ApiId=api.Id,Version="1",CreatedBy=f.User.Id};db.AddRange(project,api,version);versionId=version.Id;await db.SaveChangesAsync();}
            if(change=="permission")await db.Set<RolePermission>().Where(x=>db.Set<Permission>().Where(p=>p.Code=="api.version.write").Select(p=>p.Id).Contains(x.PermissionId)).ExecuteDeleteAsync();}
        object[] targets=[new{operationId="readOne",existingVersionId=versionId,expectedRevision=1}];using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p,targets:targets));Assert.Contains(r.StatusCode,new[]{HttpStatusCode.Conflict,HttpStatusCode.PreconditionFailed,HttpStatusCode.UnprocessableEntity,HttpStatusCode.Forbidden});await using var read=f.Context();Assert.Empty(await read.Set<ApiRoute>().ToArrayAsync());Assert.Empty(await read.Set<ApiVersionContractSources>().ToArrayAsync());
    }
    [Fact] public async Task ReferenceFailureMarksOnlyAffectedOperationUnsupported()
    {
        await using var f=await Setup();var text=ImportAndCredentialTests.Source.Replace("\"schema\":{\"type\":\"string\"}","\"schema\":{\"$ref\":\"missing.json\"}");var p=await Preview(f,Input(f,text));Assert.False(p.Operations.Single(x=>x.OperationId=="readOne").Supported);Assert.True(p.Operations.Single(x=>x.OperationId=="writeTwo").Supported);Assert.NotEmpty(p.Issues);
        using var ok=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p,targets:[new{operationId="writeTwo",newApiCode="UNRELATED",newApiName="Unrelated"}]));ok.EnsureSuccessStatusCode();
    }
    [Fact] public async Task ResponseHeaderParameterContentAndExamplesKeepReferenceBases()
    {
        await using var f=await Setup();const string text="""
        {"openapi":"3.1.0","info":{"title":"External","version":"1"},"paths":{"/external-body":{"get":{"operationId":"readOne","parameters":[{"$ref":"parts/param.json"}],"responses":{"2XX":{"$ref":"parts/response.json"}},"callbacks":{"notify":{"{$request.body#/callback}":{"post":{"responses":{"200":{"description":"ok"}}}}}}}}},"webhooks":{"hook":{"post":{"responses":{"200":{"description":"ok"}}}}}}
        """;
        object[] files=[new{name="parts/param.json",content="{\"name\":\"filter\",\"in\":\"query\",\"content\":{\"application/json\":{\"schema\":{\"$ref\":\"schemas/filter.json\"}}}}",format="json"},new{name="parts/response.json",content="{\"description\":\"ok\",\"headers\":{\"X-Mode\":{\"schema\":{\"type\":\"string\"}}},\"content\":{\"application/json\":{\"schema\":{\"$ref\":\"schemas/filter.json\"},\"examples\":{\"zero\":{\"value\":0},\"external\":{\"externalValue\":\"https://never-fetch.example/data\"}}}}}",format="json"},new{name="parts/schemas/filter.json",content="{\"type\":\"integer\"}",format="json"}];
        var p=await Preview(f,Input(f,text,files));Assert.True(Assert.Single(p.Operations).Supported);using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p));r.EnsureSuccessStatusCode();await using var db=f.Context();Assert.Single(await db.Set<ApiRoute>().ToArrayAsync());Assert.Equal("filter",(await db.Set<ApiParameter>().SingleAsync()).Name);Assert.Equal("Response-2XX",(await db.Set<ApiSchema>().SingleAsync(x=>x.SchemaType=="response")).Name);var sources=await db.Set<ApiVersionContractSources>().SingleAsync();Assert.Contains("externalValue",sources.BundleJson);Assert.Contains("callbacks",sources.BundleJson);using var metadata=System.Text.Json.JsonDocument.Parse(sources.SourcesJson);var definitions=metadata.RootElement.GetProperty("definitions");Assert.Equal(2,definitions.GetArrayLength());var uris=definitions.EnumerateArray().Select(x=>x.GetProperty("resourceUri").GetString()).ToArray();Assert.Contains("https://import.invalid/parts/param.json",uris);Assert.Contains("https://import.invalid/parts/response.json",uris);
    }
    [Fact] public async Task DeepDocumentWithinSemanticLimitPersistsInBundleEnvelope()
    {
        await using var f=await Setup();System.Text.Json.Nodes.JsonNode schema=new System.Text.Json.Nodes.JsonObject{["type"]="integer"};for(var n=0;n<29;n++)schema=new System.Text.Json.Nodes.JsonObject{["type"]="object",["properties"]=new System.Text.Json.Nodes.JsonObject{["child"]=schema}};
        var root=System.Text.Json.Nodes.JsonNode.Parse(ImportAndCredentialTests.Source)!;root["components"]!["schemas"]!["Order"]=schema;var text=root.ToJsonString(new System.Text.Json.JsonSerializerOptions{MaxDepth=64});var p=await Preview(f,Input(f,text));using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p));r.EnsureSuccessStatusCode();
    }
    [Fact] public async Task EmbeddedSchemaIdsInExternalParameterAndResponseKeepScope()
    {
        await using var f=await Setup();var root=System.Text.Json.Nodes.JsonNode.Parse(ImportAndCredentialTests.Source)!;root["paths"]!["/import/one"]!["get"]!["parameters"]=new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject{["$ref"]="parts/param.json"});
        object[] files=[new{name="parts/param.json",content="{\"name\":\"nested\",\"in\":\"query\",\"schema\":{\"$id\":\"schemas/filter.json\",\"$ref\":\"#filter\",\"$defs\":{\"value\":{\"$anchor\":\"filter\",\"type\":\"integer\"}}}}",format="json"}];
        var p=await Preview(f,Input(f,root.ToJsonString(),files));Assert.True(p.Operations.Single(x=>x.OperationId=="readOne").Supported,string.Join(";",p.Operations.Single(x=>x.OperationId=="readOne").Warnings));
    }
    private sealed class ImportClock(DateTimeOffset now):TimeProvider {public DateTimeOffset Now{get;set;}=now;public override DateTimeOffset GetUtcNow()=>Now;}
    [Fact] public async Task CommittedContentHasTwentyMinutesFromCommit()
    {
        var clock=new ImportClock(DateTimeOffset.UtcNow);await using var f=new ApiFixture();await f.InitializeAsync(b=>b.Services.AddSingleton<TimeProvider>(clock));await f.SeedCatalogAsync();using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();var p=await Preview(f);clock.Now=clock.Now.AddMinutes(19);
        using var commit=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p));commit.EnsureSuccessStatusCode();await using(var db=f.Context())Assert.InRange(((await db.Set<ApiImportPreview>().SingleAsync()).ExpiresAt-clock.Now.AddMinutes(20)).Duration(),TimeSpan.Zero,TimeSpan.FromMicroseconds(1));
        clock.Now=clock.Now.AddMinutes(2);using var services=f.Services();Assert.Equal(0,await services.ServiceProvider.GetRequiredService<ImportPreviewCleanupService>().RunAsync(100,default));
    }
    [Fact] public async Task LegacyTextImportAlsoUsesProjectResourceBudget()
    {
        await using var f=await Setup();using var policy=await f.WriteAsync(HttpMethod.Put,$"/api/v1/projects/{f.Project.Id}/import-source-policy",new SaveImportSourcePolicyRequest([],new(MaxDocumentBytes:100)),"\"0\"");policy.EnsureSuccessStatusCode();using var r=await f.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-preview",new{projectId=f.Project.Id,environmentId=f.Environment.Id,clusterId=f.Cluster.Id,source=ImportAndCredentialTests.Source});Assert.Equal(HttpStatusCode.RequestEntityTooLarge,r.StatusCode);
    }
    [Fact] public async Task PreviewAuditHasHashesAndNoRawContractExample()
    {
        await using var f=await Setup();const string marker="private-contract-example-not-for-audit";var text=ImportAndCredentialTests.Source.Replace("\"title\":\"Orders\"","\"title\":\"Orders\",\"x-private-example\":\""+marker+"\"");var p=await Preview(f,Input(f,text));await using var db=f.Context();var row=await db.Set<AuditLog>().SingleAsync(x=>x.Action=="openapi.preview.create");var audit=row.BeforeJson+row.AfterJson;Assert.Contains(p.SourceHash,audit);Assert.Contains(p.BundleHash,audit);Assert.DoesNotContain(marker,audit);Assert.DoesNotContain("openapi",row.AfterJson!,StringComparison.OrdinalIgnoreCase);
    }
    [Theory][InlineData("[]")][InlineData("123")][InlineData("null")]
    public async Task InvalidParameterContentCannotBecomeAStringSchema(string content)
    {
        await using var f=await Setup();var root=System.Text.Json.Nodes.JsonNode.Parse(ImportAndCredentialTests.Source)!;var parameter=(System.Text.Json.Nodes.JsonObject)root["paths"]!["/import/one"]!["get"]!["parameters"]![1]!;parameter.Remove("schema");parameter["content"]=System.Text.Json.Nodes.JsonNode.Parse(content);var p=await Preview(f,Input(f,root.ToJsonString()));Assert.False(p.Operations.Single(x=>x.OperationId=="readOne").Supported);Assert.True(p.Operations.Single(x=>x.OperationId=="writeTwo").Supported);
    }
    private sealed class LoopbackDns:IContractDnsResolver {public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct)=>Task.FromResult(new[]{IPAddress.Loopback});}
    private sealed class ChangingSource:IAsyncDisposable
    {
        private readonly TcpListener listener=new(IPAddress.Loopback,0);private readonly CancellationTokenSource lifetime=new();private readonly Task loop;
        public Uri Origin{get;}public string Text{get;set;}=ImportAndCredentialTests.Source;public int Calls{get;private set;}public bool FailExternal{get;set;}
        public ChangingSource(){listener.Start();Origin=new($"http://contracts.test:{((IPEndPoint)listener.LocalEndpoint).Port}");loop=Task.Run(async()=>{try{while(true){using var c=await listener.AcceptTcpClientAsync(lifetime.Token);await using var s=c.GetStream();using var reader=new StreamReader(s,Encoding.ASCII,leaveOpen:true);var requestLine=await reader.ReadLineAsync(lifetime.Token);while(await reader.ReadLineAsync(lifetime.Token) is {Length:>0}){}Calls++;var status=FailExternal&&requestLine?.Contains("/contracts/openapi.json")!=true?503:200;var b=Encoding.UTF8.GetBytes(Text);await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n"),lifetime.Token);await s.WriteAsync(b,lifetime.Token);}}catch(OperationCanceledException){}});}
        public async ValueTask DisposeAsync(){lifetime.Cancel();listener.Stop();await loop;lifetime.Dispose();}
    }
    [Fact] public async Task CommitUsesPreviewBytesAfterUrlChanges()
    {
        await using var source=new ChangingSource();await using var f=new ApiFixture();var origin=source.Origin.GetLeftPart(UriPartial.Authority);
        await f.InitializeAsync(b=>{b.Configuration["ContractImport:AllowHttp"]="true";b.Configuration["ContractImport:FixtureEnabled"]="true";b.Configuration["ContractImport:FixtureOrigins:0"]=origin;b.Configuration["ContractImport:AllowedPrivateCidrs:0"]="127.0.0.1/32";b.Services.AddSingleton<IContractDnsResolver,LoopbackDns>();});await f.SeedCatalogAsync();using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();using var policy=await f.WriteAsync(HttpMethod.Put,$"/api/v1/projects/{f.Project.Id}/import-source-policy",new SaveImportSourcePolicyRequest([new(origin,"/contracts",["127.0.0.1/32"])],new()),"\"0\"");policy.EnsureSuccessStatusCode();
        var p=await Preview(f,new {projectId=f.Project.Id,environmentId=f.Environment.Id,clusterId=f.Cluster.Id,sourceUrl=new Uri(source.Origin,"/contracts/openapi.json").AbsoluteUri});source.Text=ImportAndCredentialTests.Source.Replace("/import/one","/changed");using var r=await f.WriteAsync(HttpMethod.Post,$"/api/v1/openapi/import-previews/{p.PreviewId}/commit",Commit(p));r.EnsureSuccessStatusCode();Assert.Equal(1,source.Calls);await using var db=f.Context();Assert.Equal("/import/one",(await db.Set<ApiRoute>().SingleAsync()).Path);
    }
    [Fact] public async Task FailedExternalSourcesStillCountAgainstNetworkResourceBudget()
    {
        await using var source=new ChangingSource{FailExternal=true};var root=System.Text.Json.Nodes.JsonNode.Parse(ImportAndCredentialTests.Source)!;for(var n=0;n<20;n++)root["components"]!["schemas"]!["Missing"+n]=new System.Text.Json.Nodes.JsonObject{["$ref"]="missing"+n+".json"};source.Text=root.ToJsonString();await using var f=new ApiFixture();var origin=source.Origin.GetLeftPart(UriPartial.Authority);
        await f.InitializeAsync(b=>{b.Configuration["ContractImport:AllowHttp"]="true";b.Configuration["ContractImport:FixtureEnabled"]="true";b.Configuration["ContractImport:FixtureOrigins:0"]=origin;b.Configuration["ContractImport:AllowedPrivateCidrs:0"]="127.0.0.1/32";b.Services.AddSingleton<IContractDnsResolver,LoopbackDns>();});await f.SeedCatalogAsync();using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();using var policy=await f.WriteAsync(HttpMethod.Put,$"/api/v1/projects/{f.Project.Id}/import-source-policy",new SaveImportSourcePolicyRequest([new(origin,"/contracts",["127.0.0.1/32"])],new()),"\"0\"");policy.EnsureSuccessStatusCode();
        using var r=await f.WriteAsync(HttpMethod.Post,"/api/v1/openapi/import-previews",new{projectId=f.Project.Id,environmentId=f.Environment.Id,clusterId=f.Cluster.Id,sourceUrl=new Uri(source.Origin,"/contracts/openapi.json").AbsoluteUri});Assert.Equal(HttpStatusCode.UnprocessableEntity,r.StatusCode);Assert.True(source.Calls<=16);await using var db=f.Context();Assert.Empty(await db.Set<ApiImportPreview>().ToArrayAsync());
    }
}

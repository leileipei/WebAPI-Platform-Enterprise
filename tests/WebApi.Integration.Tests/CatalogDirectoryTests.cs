using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CatalogDirectoryTests(MigratedDatabaseTemplateFixture template) : IClassFixture<MigratedDatabaseTemplateFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportReusesMappedRouteWithoutReplacingManualSettings(bool enabled)
    {
        await using var f = await ImportSessionTests.Setup(template.Template);
        var body = new SaveRouteRequest(null, f.Version.Id, "Manual", "/manual/{key}", ["GET","PATCH"], f.Cluster.Id,
            Priority:350, Enabled:enabled, TimeoutMs:8500);
        using var created = await f.WriteAsync(HttpMethod.Post, $"/api/v1/environments/{f.Environment.Id}/routes", body);
        created.EnsureSuccessStatusCode();
        var route = (await created.Content.ReadFromJsonAsync<RouteDto>())!;
        var preview = await ImportSessionTests.Preview(f);
        var target = new { operationId="readOne", existingVersionId=f.Version.Id, expectedRevision=f.Version.Revision,
            existingRouteId=route.Id, expectedRouteRevision=route.Revision };
        using var commit = await f.WriteAsync(HttpMethod.Post, $"/api/v1/openapi/import-previews/{preview.PreviewId}/commit",
            ImportSessionTests.Commit(preview, targets:[target]));
        commit.EnsureSuccessStatusCode();
        var actual = (await f.Client.GetFromJsonAsync<RouteDto>($"/api/v1/routes/{route.Id}"))!;
        Assert.Equal((route.RouteName,route.Path,route.Priority,enabled,8500,route.ClusterId), (actual.RouteName,actual.Path,actual.Priority,actual.Enabled,actual.TimeoutMs,actual.ClusterId));
        Assert.Equal(route.Methods.ToArray(), actual.Methods.ToArray());
        await using var db=f.Context();
        Assert.Equal(enabled ? 2 : 0, await db.Set<RouteMethod>().CountAsync(r=>r.RouteId==route.Id));
        Assert.Empty(await db.Set<GatewayConfigSnapshot>().ToArrayAsync());
    }

    [Fact]
    public async Task MismatchedExplicitParameterTypeIsRejectedAndSelectedSchemaValidatesRealExamples()
    {
        var setup=await SchemaValidationApiTests.Setup(databaseTemplate:template.Template);
        await using var f=setup.Fixture;
        string path=$"/api/v1/versions/{f.Version.Id}/parameters";
        var bad = new SaveParameterRequest(null,"query","quantity","integer",false,"{\"type\":\"string\"}");
        using var conflict=await f.WriteAsync(HttpMethod.Put,path,new[]{bad},"\"1\"");
        Assert.Equal(HttpStatusCode.UnprocessableEntity,conflict.StatusCode);
        var schema="""{"type":"integer","minimum":1,"description":"units"}""";
        using var corrected=await f.WriteAsync(HttpMethod.Put,path,new[]{bad with {Schema=schema}},"\"1\"");
        corrected.EnsureSuccessStatusCode();
        var saved=Assert.Single((await f.Client.GetFromJsonAsync<ParameterDto[]>(path))!);
        Assert.Equal("integer",saved.DataType);
        Assert.Equal(1,JsonDocument.Parse(saved.Schema!).RootElement.GetProperty("minimum").GetInt32());
        foreach(var (example,expected) in new[]{("42","Valid"),("\"42\"","Invalid")}) {
            using var response=await f.WriteAsync(HttpMethod.Post,$"/api/v1/versions/{f.Version.Id}/schema-validation",
                new {expectedVersionRevision=2,parameterId=saved.Id,exampleJson=example,direction="request",formatMode="Annotation"});
            response.EnsureSuccessStatusCode();
            Assert.Equal(expected,(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task MaintainingAReferencedParameterPreservesItsReferenceAndResolution()
    {
        var setup=await SchemaValidationApiTests.Setup(databaseTemplate:template.Template);
        await using var f=setup.Fixture;
        var reference="""{"$ref":"#/components/schemas/Value"}""";
        using var save=await f.WriteAsync(HttpMethod.Put,$"/api/v1/versions/{f.Version.Id}/parameters",
            new[]{new SaveParameterRequest(setup.Parameter.Id,"query","quantity","unknown",false,reference)},"\"1\"");
        save.EnsureSuccessStatusCode();
        var row=Assert.Single((await save.Content.ReadFromJsonAsync<ParameterDto[]>())!);
        Assert.Equal("#/components/schemas/Value",JsonDocument.Parse(row.Schema!).RootElement.GetProperty("$ref").GetString());
        using var check=await f.WriteAsync(HttpMethod.Post,SchemaValidationApiTests.Path(f),SchemaValidationApiTests.Body(null,"7",2,parameterId:row.Id));
        check.EnsureSuccessStatusCode();
        Assert.Equal("Valid",(await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task DirectoryFiltersTheProjectBeforePagingAndProjectsOnlySelectedEnvironmentData()
    {
        await using var f=await ImportSessionTests.Setup(template.Template);
        await using(var db=f.Context()){
            for(var i=0;i<51;i++)db.Add(new Api {OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,OwnerUserId=f.User.Id,Code=$"AAA{i:00}",Name=$"部件{i}",LifecycleStatus="Draft"});
            var role=await db.Set<UserRole>().Where(r=>r.UserId==f.User.Id).Select(r=>r.RoleId).SingleAsync();
            var metrics=new Permission{Code="metrics.read",Name="Metrics",Module="metrics"};db.Add(metrics);db.Add(new RolePermission{RoleId=role,PermissionId=metrics.Id});
            var version=await db.Set<ApiVersion>().SingleAsync(v=>v.Id==f.Version.Id);
            version.OpenapiDocument="""{"paths":{"/order":{"get":{"tags":["inventory"]}}}}""";
            await db.SaveChangesAsync();
        }
        using var created=await f.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{f.Environment.Id}/routes",f.RouteBody("/directory","PATCH"));
        created.EnsureSuccessStatusCode();
        var list=$"/api/v1/projects/{f.Project.Id}/apis?directory=true&environmentId={f.Environment.Id}";
        foreach(var filter in new[]{$"q={f.Api.Code}","method=PATCH",$"version={f.Version.Version}","tag=inventory",$"owner={f.User.Id}&q={f.Api.Code}","health=Unknown&q="+f.Api.Code}){
            var result=await f.Client.GetFromJsonAsync<JsonElement>(list+"&"+filter);
            Assert.Equal(1,result.GetProperty("total").GetInt32());
            var row=Assert.Single(result.GetProperty("items").EnumerateArray());
            Assert.Equal(f.Api.Id,row.GetProperty("id").GetGuid());
            Assert.Equal(new[]{"PATCH"},row.GetProperty("methods").EnumerateArray().Select(e=>e.GetString()));
            Assert.Contains("inventory",row.GetProperty("tags").EnumerateArray().Select(e=>e.GetString()));
            Assert.Equal("Unknown",row.GetProperty("health").GetString());
        }
        using var export=await f.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{f.Project.Id}/apis/export?environmentId={f.Environment.Id}",new{ids=new[]{f.Api.Id}});
        export.EnsureSuccessStatusCode();Assert.Equal(f.Api.Id,Assert.Single((await export.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()).GetProperty("id").GetGuid());
        using var mixed=await f.WriteAsync(HttpMethod.Post,$"/api/v1/projects/{f.Project.Id}/apis/export",new{ids=new[]{f.Api.Id,Guid.NewGuid()}});
        Assert.Equal(HttpStatusCode.Conflict,mixed.StatusCode);
        using var foreign=await f.Client.GetAsync($"/api/v1/projects/{f.Project.Id}/apis?directory=true&environmentId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound,foreign.StatusCode);
    }

    [Fact]
    public async Task RetiringOneVersionPreservesOtherVersionsAndTheSealedContract()
    {
        await using var f=await ImportSessionTests.Setup(template.Template);
        using var other=await f.WriteAsync(HttpMethod.Post,$"/api/v1/apis/{f.Api.Id}/versions",new{version="next"});
        other.EnsureSuccessStatusCode();
        await using(var db=f.Context()){await db.Set<ApiVersion>().Where(v=>v.Id==f.Version.Id).ExecuteUpdateAsync(s=>s.SetProperty(v=>v.Status,"Published").SetProperty(v=>v.SealedAt,DateTimeOffset.UtcNow));}
        var path=$"/api/v1/versions/{f.Version.Id}/retire";
        using var stale=await f.WriteAsync(HttpMethod.Post,path,new{},"\"0\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        using var retired=await f.WriteAsync(HttpMethod.Post,path,new{},"\"1\"");retired.EnsureSuccessStatusCode();
        var rows=(await f.Client.GetFromJsonAsync<VersionDto[]>($"/api/v1/apis/{f.Api.Id}/versions"))!;
        Assert.Equal("Draft",rows.Single(v=>v.Id!=f.Version.Id).Status);
        var version=rows.Single(v=>v.Id==f.Version.Id);Assert.Equal("Retired",version.Status);Assert.Equal(2,version.Revision);Assert.NotNull(version.SealedAt);
    }

    [Fact]
    public async Task CopyRequiresTheTargetClusterAndPreservesTheSourceWithOneIdempotentResult()
    {
        await using var f=await ImportSessionTests.Setup(template.Template);
        var env=new EnvironmentRecord{ProjectId=f.Project.Id,Code="OTHER",Name="Target"};
        var cluster=new UpstreamCluster{ProjectId=f.Project.Id,EnvironmentId=env.Id,Name="Target",LoadBalancingPolicy="RoundRobin",HealthCheckPath="/",HealthCheckIntervalSec=30};
        await using(var db=f.Context()){db.AddRange(env,cluster);await db.SaveChangesAsync();}
        using var original=await f.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{f.Environment.Id}/routes",new SaveRouteRequest(null,f.Version.Id,"Orders","/copy",["PATCH"],f.Cluster.Id,Priority:350,TimeoutMs:8500));
        original.EnsureSuccessStatusCode();var src=(await original.Content.ReadFromJsonAsync<RouteDto>())!;
        var path=$"/api/v1/routes/{src.Id}/copy";
        using var invalid=await f.WriteAsync(HttpMethod.Post,path,new{environmentId=env.Id,clusterId=f.Cluster.Id},original.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,invalid.StatusCode);
        var key=Guid.NewGuid().ToString();
        async Task<RouteDto> Copy(){using var req=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(new{environmentId=env.Id,clusterId=cluster.Id})};req.Headers.Add("Idempotency-Key",key);req.Headers.Add("X-CSRF-Token",await f.CsrfAsync());req.Headers.Add("If-Match",original.Headers.ETag.Tag);using var r=await f.Client.SendAsync(req);Assert.True(r.IsSuccessStatusCode,await r.Content.ReadAsStringAsync());return (await r.Content.ReadFromJsonAsync<RouteDto>())!;}
        var row=await Copy();Assert.Equal(row.Id,(await Copy()).Id);
        Assert.NotEqual(src.Id,row.Id);
        Assert.Equal((env.Id,cluster.Id,src.Priority,src.TimeoutMs,src.Enabled,src.RequireApiKey),(row.EnvironmentId,row.ClusterId,row.Priority,row.TimeoutMs,row.Enabled,row.RequireApiKey));
        Assert.Equal(src.Methods.ToArray(),row.Methods.ToArray());
        await using var check=f.Context();Assert.Equal(2,await check.Set<ApiRoute>().CountAsync());Assert.Empty(await check.Set<GatewayConfigSnapshot>().ToArrayAsync());
    }

    [Fact]
    public async Task OverviewShowsOnlyRelatedApiReleasesInTheRequestedEnvironment()
    {
        await using var f=new ApiFixture(template.Template);await f.InitializeAsync();await f.SeedReleaseAsync();using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();
        await ObservationTestSupport.GrantAsync(f,["metrics.read"]);
        using var draft=await f.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{f.Environment.Id}/releases",await f.PreviewReleaseRequestAsync());
        draft.EnsureSuccessStatusCode();
        var expected=(await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var path=$"/api/v1/apis/{f.Api.Id}/overview?environmentId=";
        using var response=await f.Client.GetAsync(path+f.Environment.Id);response.EnsureSuccessStatusCode();
        var result=await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected,Assert.Single(result.GetProperty("releases").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal("Unconfirmed",result.GetProperty("runningState").GetString());
        Assert.Equal(HttpStatusCode.NotFound,(await f.Client.GetAsync(path+Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task OverviewAndHealthUseTheAcknowledgedRoutesAndProbesAfterDraftChanges()
    {
        await using var scenario=new DeploymentScenario();
        var probes=new ProbeSource(scenario);
        await scenario.InitializeAsync(builder=>{
            builder.Configuration["Observability:Enabled"]="true";
            builder.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>
                o.HttpMessageHandlerBuilderActions.Add(b=>b.PrimaryHandler=probes));
        });
        var f=scenario.Api;
        await ObservationTestSupport.GrantAsync(f,["metrics.read"]);
        await using(var db=f.Context()){
            var role=await db.Set<UserRole>().Where(r=>r.UserId==f.User.Id).Select(r=>r.RoleId).FirstAsync();
            var permission=new Permission{Code="policy.read",Name="策略",Module="policy"};
            db.Add(permission);db.Add(new RolePermission{RoleId=role,PermissionId=permission.Id});
            await db.SaveChangesAsync();
        }
        var release=await scenario.PublishAsync();
        await using(var db=f.Context()){
            var route=await db.Set<ApiRoute>().SingleAsync();
            route.Path="/future";route.TimeoutMs=6500;route.Revision++;
            await db.SaveChangesAsync();
        }
        var path=$"/api/v1/apis/{f.Api.Id}/overview?environmentId={f.Environment.Id}";
        var overview=await f.Client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal("Confirmed",overview.GetProperty("runningState").GetString());
        var policy=Assert.Single(overview.GetProperty("routePolicies").EnumerateArray());
        Assert.Equal("/orders",policy.GetProperty("path").GetString());Assert.Equal(30000,policy.GetProperty("timeoutMs").GetInt32());
        Assert.Equal("ApiKey",policy.GetProperty("authentication").GetString());
        var cluster=Assert.Single(overview.GetProperty("clusters").EnumerateArray());
        Assert.Equal(f.Cluster.Id,cluster.GetProperty("clusterId").GetGuid());Assert.Equal("Healthy",cluster.GetProperty("health").GetString());
        Assert.Equal(release,Assert.Single(overview.GetProperty("releases").EnumerateArray()).GetProperty("id").GetGuid());
        var directory=$"/api/v1/projects/{f.Project.Id}/apis?directory=true&environmentId={f.Environment.Id}&health=";
        Assert.Equal(1,(await f.Client.GetFromJsonAsync<JsonElement>(directory+"Healthy")).GetProperty("total").GetInt32());
        probes.OmitNodeName=true;
        Assert.Equal("Unknown",Assert.Single((await f.Client.GetFromJsonAsync<JsonElement>(path)).GetProperty("clusters").EnumerateArray()).GetProperty("health").GetString());
        probes.OmitNodeName=false;
        probes.Unhealthy=true;
        Assert.Equal("Unhealthy",Assert.Single((await f.Client.GetFromJsonAsync<JsonElement>(path)).GetProperty("clusters").EnumerateArray()).GetProperty("health").GetString());
        Assert.Equal(1,(await f.Client.GetFromJsonAsync<JsonElement>(directory+"Unhealthy")).GetProperty("total").GetInt32());
        probes.WrongInstance=true;
        Assert.Equal(1,(await f.Client.GetFromJsonAsync<JsonElement>(directory+"Unknown")).GetProperty("total").GetInt32());
        await using(var db=f.Context()){
            var permission=await db.Set<Permission>().Where(p=>p.Code=="cluster.read").Select(p=>p.Id).SingleAsync();
            await db.Set<RolePermission>().Where(g=>g.PermissionId==permission).ExecuteDeleteAsync();
        }
        Assert.Equal(JsonValueKind.Null,(await f.Client.GetFromJsonAsync<JsonElement>(path)).GetProperty("clusters").ValueKind);
        using var denied=await f.Client.GetAsync(directory+"Unknown");Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
    }

    private sealed class ProbeSource(DeploymentScenario scenario) : HttpMessageHandler
    {
        public bool Unhealthy {get;set;}
        public bool WrongInstance {get;set;}
        public bool OmitNodeName {get;set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var f=scenario.Api;
            var stamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var samples=(from i in Enumerable.Range(0,2) from component in new[]{"active","passive"} select new {
                metric=new Dictionary<string,string>{
                    ["service_instance_id"]="node-"+i,
                    ["webapi_environment_id"]=f.Environment.Id.ToString(),["webapi_node_instance"]=WrongInstance?Guid.NewGuid().ToString():scenario.Instances[i].ToString(),
                    ["webapi_deployment_sequence"]="1",["webapi_cluster_id"]=f.Cluster.Id.ToString(),["webapi_destination_id"]=f.Destination.Id.ToString(),
                    ["webapi_health_target"]=GatewayProbeTarget.Fingerprint(f.Destination.Address,f.Cluster.HealthCheckPath),["webapi_health_component"]=component},
                value=new object[]{stamp,Unhealthy&&i==0&&component=="passive"?"0":"1"}}).ToArray();
            if(OmitNodeName)
                foreach(var sample in samples) sample.metric.Remove("service_instance_id");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{status="success",data=new{resultType="vector",result=samples}})});
        }
    }
}

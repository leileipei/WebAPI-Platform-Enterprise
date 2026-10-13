using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Gateway.Tests;
public sealed class TransformPipelineTests
{
    internal static string Config(string? remove=null,string? add=null,bool query=false,bool headers=true)=>JsonSerializer.Serialize(new{
        requestHeaders=headers?new[]{new{action="Set",name="X-Partner-Version",value="v2"},new{action="Set",name="X-Skip",value="must-not-return"}}:[],
        responseHeaders=headers?new[]{new{action="Set",name="X-Upstream-Service",value="inventory"},new{action="Set",name="X-Skip",value="must-not-return"}}:[],
        path=remove is null&&add is null?null:new{removePrefix=remove,addPrefix=add},
        query=query?new object[]{new{action="Remove",name="debug"},new{action="Set",name="region",value="cn"}}:[],requestBody=(object?)null,responseBody=(object?)null});
    internal static async Task<Policy> BindAsync(GatewayFixture f,string config)
    {await using var db=f.Control.Context();var p=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="Transform"+Guid.NewGuid().ToString("N"),Type="transform",Config=config};db.Add(p);db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=p.Id,Priority=1});(await db.Set<ApiRoute>().SingleAsync(r=>r.Id==f.RouteId)).Revision++;await db.SaveChangesAsync();return p;}
    internal static async Task RouteAsync(GatewayFixture f,string path,string method="GET",int timeoutMs=30000)
    {using var current=await f.Control.Client.GetAsync($"/api/v1/routes/{f.RouteId}");using var updated=await f.Control.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{f.RouteId}",new{apiVersionId=f.Control.Version.Id,routeName="Orders",path,methods=new[]{method},clusterId=f.Control.Cluster.Id,priority=100,enabled=true,timeoutMs,requireApiKey=true},current.Headers.ETag!.ToString());updated.EnsureSuccessStatusCode();}
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Transform_RealProxy_RewritesOnlyBackendCopyAndSkipsDynamicHeaders(bool retry)
    {
        var hits=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>
        {Interlocked.Increment(ref hits);ctx.Response.StatusCode=503;ctx.Response.Headers.Connection="X-Skip";ctx.Response.Headers["X-Skip"]="upstream-hop";ctx.Response.Headers["X-Upstream-Service"]="old";await ctx.Response.WriteAsJsonAsync(new{path=ctx.Request.Path.Value,query=ctx.Request.QueryString.Value,partner=ctx.Request.Headers["X-Partner-Version"].ToString(),skip=ctx.Request.Headers["X-Skip"].ToString(),apiKey=ctx.Request.Headers.ContainsKey("X-API-Key"),method=ctx.Request.Method,sequence=ctx.Request.Headers["X-WebApi-Deployment-Sequence"].ToString()});})};
        await f.InitializeAsync();await RouteAsync(f,"/gateway/stock");await BindAsync(f,Config("/gateway","/internal/v2",true));
        if(retry){await using var db=f.Control.Context();var p=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="Retry",Type="retry",Config="{\"maxAttempts\":2,\"perAttemptTimeoutMs\":1000,\"baseDelayMs\":0,\"maxDelayMs\":0,\"jitterPercent\":0,\"retryStatusCodes\":[503],\"retryConnectionFailures\":false}"};db.Add(p);db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=p.Id,Priority=2});await db.SaveChangesAsync();}
        var published=await f.PublishAsync();await f.ApplyBothAsync(published);
        foreach(var node in new[]{0,1})
        {using var request=new HttpRequestMessage(HttpMethod.Get,"/gateway/stock?sku=A&sku=B&debug=1&Region=US");request.Headers.Add("X-API-Key",f.Credential);request.Headers.Add("Connection","X-Skip");request.Headers.Add("X-Skip","client-hop");request.Headers.Add("X-WebApi-Deployment-Sequence","forged");using var response=await f.Clients[node].SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
            Assert.Equal("inventory",response.Headers.GetValues("X-Upstream-Service").Single());
            Assert.False(response.Headers.Contains("X-Skip"));var body=(await response.Content.ReadFromJsonAsync<JsonElement>());
            Assert.Equal("/internal/v2/stock",body.GetProperty("path").GetString());
            Assert.Equal("?sku=A&sku=B&Region=US&region=cn",body.GetProperty("query").GetString());
            Assert.Equal("v2",body.GetProperty("partner").GetString());
            Assert.Equal("",body.GetProperty("skip").GetString());
            Assert.False(body.GetProperty("apiKey").GetBoolean());
            Assert.Equal("GET",body.GetProperty("method").GetString());
            Assert.Equal(published.Envelope.DeploymentSequence.ToString(),body.GetProperty("sequence").GetString());}
        Assert.Equal(retry?4:2,hits);
    }
    [Fact]
    public async Task Transform_PathMismatch_Is422BeforeBackend_AndAuthErrorHasNoBusinessHeader()
    {var hits=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref hits);await next(ctx);})};await f.InitializeAsync();await BindAsync(f,Config("/gateway","/internal"));await f.ApplyBothAsync(await f.PublishAsync());foreach(var node in new[]{0,1}){using var denied=await f.RequestAsync(node,key:"invalid.invalid");
            Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
            Assert.False(denied.Headers.Contains("X-Upstream-Service"));using var mismatch=await f.RequestAsync(node);
            Assert.Equal(HttpStatusCode.UnprocessableEntity,mismatch.StatusCode);
            Assert.False(mismatch.Headers.Contains("X-Upstream-Service"));var json=(await mismatch.Content.ReadFromJsonAsync<JsonElement>());
            Assert.Equal("transform_path_mismatch",json.GetProperty("reason").GetString());
            Assert.Equal("transform_rejected",json.GetProperty("code").GetString());}Assert.Equal(0,hits);}
    [Fact]
    public async Task Transform_DifferentApiGrantCannotAuthorizeOriginalRoute()
    {
        await using var f=new GatewayFixture();await f.InitializeAsync();await BindAsync(f,Config("/orders","/another-api"));
        var other=new Api{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Code="other-api",Name="Other",LifecycleStatus="Draft",OwnerUserId=f.Control.Api.OwnerUserId};
        await using(var db=f.Control.Context())
        {
            var grant=await db.Set<ApplicationApiPermission>().SingleAsync();grant.ValidFrom=DateTimeOffset.UtcNow.AddHours(1);db.Add(other);
            db.Add(new ApplicationApiPermission{ApplicationId=f.Control.Application.Id,EnvironmentId=f.Control.Environment.Id,ApiId=other.Id,ValidFrom=DateTimeOffset.UtcNow.AddHours(-1),CreatedBy=f.Control.Api.OwnerUserId});await db.SaveChangesAsync();
        }
        using var version=await f.Control.WriteAsync(HttpMethod.Post,$"/api/v1/apis/{other.Id}/versions",new{version="1.0.0",changeType="compatible"});version.EnsureSuccessStatusCode();var id=(await version.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var route=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(f.Control.RouteBody("/another-api")))!;route["apiVersionId"]=id;
        using var created=await f.Control.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{f.Control.Environment.Id}/routes",route);created.EnsureSuccessStatusCode();
        await f.ApplyBothAsync(await f.PublishAsync(extraVersions:[id]));
        foreach(var node in new[]{0,1})
        {
            using var permitted=await f.RequestAsync(node,path:"/another-api");permitted.EnsureSuccessStatusCode();
            using var denied=await f.RequestAsync(node);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.False(denied.Headers.Contains("X-Upstream-Service"));Assert.Contains("api_not_granted",await denied.Content.ReadAsStringAsync());
        }
    }
    [Fact]
    public async Task Transform_HeaderOnly_PreservesStreamingAndContentLength()
    {
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>
        {ctx.Response.ContentType="application/octet-stream";ctx.Response.ContentLength=6;await ctx.Response.Body.WriteAsync("abc"u8.ToArray());await ctx.Response.Body.FlushAsync();await release.Task.WaitAsync(ctx.RequestAborted);await ctx.Response.Body.WriteAsync("def"u8.ToArray());})};
        await f.InitializeAsync();await BindAsync(f,Config());await f.ApplyBothAsync(await f.PublishAsync());
        using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Add("X-API-Key",f.Credential);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var response=await f.Clients[0].SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);response.EnsureSuccessStatusCode();
            Assert.Equal(6,response.Content.Headers.ContentLength);Assert.Equal("inventory",response.Headers.GetValues("X-Upstream-Service").Single());
            await using var body=await response.Content.ReadAsStreamAsync(deadline.Token);var first=new byte[3];await body.ReadExactlyAsync(first,deadline.Token);Assert.Equal("abc",System.Text.Encoding.UTF8.GetString(first));
            release.TrySetResult();var second=new byte[3];await body.ReadExactlyAsync(second,deadline.Token);Assert.Equal("def",System.Text.Encoding.UTF8.GetString(second));
        }
        finally{release.TrySetResult();}
    }

}

using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using WebApi.Gateway.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Runtime;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class CorsPipelineTests
{
    internal static string Config(int age=600,bool wildcard=false,bool credentials=false,string[]? methods=null)=>JsonSerializer.Serialize(new {allowedOrigins=new[]{wildcard?"*":"https://portal.example.com"},allowCredentials=credentials,allowedMethods=methods??["GET","POST","OPTIONS"],allowedHeaders=new[]{"Authorization","X-API-Key","Content-Type","X-Business"},exposedHeaders=new[]{"X-Upstream"},maxAgeSeconds=age});
    internal static async Task<Policy> BindAsync(GatewayFixture f,Guid? route=null,int age=600,bool wildcard=false,bool credentials=false,string[]? methods=null)
    {await using var db=f.Control.Context();var p=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="CORS"+Guid.NewGuid().ToString("N"),Type="cors",Config=Config(age,wildcard,credentials,methods)};db.Add(p);db.Add(new RoutePolicyBinding{RouteId=route??f.RouteId,PolicyId=p.Id,Priority=1});(await db.Set<ApiRoute>().SingleAsync(r=>r.Id==(route??f.RouteId))).Revision++;await db.SaveChangesAsync();return p;}
    private static HttpRequestMessage Preflight(string method="GET",string origin="https://portal.example.com",string? headers="X-API-Key, Content-Type")
    {var request=new HttpRequestMessage(HttpMethod.Options,"/orders");request.Headers.Add("Origin",origin);request.Headers.Add("Access-Control-Request-Method",method);if(headers is not null)request.Headers.Add("Access-Control-Request-Headers",headers);return request;}

    [Fact]
    public async Task Cors_GetOnlyPreflight_Is204WithoutAuthenticationCacheOrBackend()
    {
        var hits=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref hits);await next(ctx);})};await f.InitializeAsync();await BindAsync(f,methods:["GET"]);var published=await f.PublishAsync();await f.ApplyBothAsync(published);
        foreach(var node in new[]{0,1}){using var request=Preflight();using var response=await f.Clients[node].SendAsync(request);Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);Assert.Equal("https://portal.example.com",response.Headers.GetValues("Access-Control-Allow-Origin").Single());Assert.Equal("GET",response.Headers.GetValues("Access-Control-Allow-Methods").Single());Assert.Equal(published.Envelope.DeploymentSequence.ToString(),response.Headers.GetValues("X-WebApi-Deployment-Sequence").Single());Assert.Equal("",await response.Content.ReadAsStringAsync());}
        Assert.Equal(0,hits);
        foreach(var gateway in f.Gateways){var provider=gateway.Services.GetRequiredService<EnterpriseProxyConfigProvider>();Assert.Equal(2,provider.Current.Routes.Count);Assert.Single(provider.Current.Routes,r=>r.Match.Methods!.SequenceEqual(new[]{"OPTIONS"}));}
    }

    [Theory]
    [InlineData("https://evil.example.com","GET","X-API-Key")]
    [InlineData("https://portal.example.com","GET","X-Unknown")]
    [InlineData("https://portal.example.com","GET","X-Business,x-business")]
    [InlineData("https://portal.example.com","GET,POST","X-API-Key")]
    public async Task Cors_MatchedDisallowedOrMalformedPreflight_Is403(string origin,string method,string headers)
    {
        var hits=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref hits);await next(ctx);})};await f.InitializeAsync();await BindAsync(f);await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1}){using var request=Preflight(method,origin,headers);using var response=await f.Clients[node].SendAsync(request);Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("cors_rejected",json.RootElement.GetProperty("code").GetString());}Assert.Equal(0,hits);
    }

    [Fact]
    public async Task Cors_AllowedOrigin401_AndUpstream503_KeepOwnedHeaders()
    {
        var hits=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref hits);ctx.Response.StatusCode=503;ctx.Response.Headers["Access-Control-Allow-Origin"]="https://forged.example.com";ctx.Response.Headers["Access-Control-Allow-Credentials"]="false";ctx.Response.Headers.Vary="Accept-Language";await next(ctx);})};await f.InitializeAsync();await BindAsync(f,credentials:true);await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1})foreach(var key in new[]{"invalid.invalid",f.Credential}){using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Add("Origin","https://portal.example.com");request.Headers.Add("X-API-Key",key);using var response=await f.Clients[node].SendAsync(request);Assert.Equal(key==f.Credential?HttpStatusCode.ServiceUnavailable:HttpStatusCode.Unauthorized,response.StatusCode);Assert.Equal("https://portal.example.com",response.Headers.GetValues("Access-Control-Allow-Origin").Single());Assert.Equal("true",response.Headers.GetValues("Access-Control-Allow-Credentials").Single());Assert.Contains("Origin",response.Headers.Vary);if(key==f.Credential)Assert.Contains("Accept-Language",response.Headers.Vary);}Assert.Equal(2,hits);
    }

    [Fact]
    public async Task Cors_UnmatchedMethodsAndNoOrigin_KeepNativeRoutingAndAuth()
    {
        await using var f=new GatewayFixture();await f.InitializeAsync();await BindAsync(f,methods:["GET"]);await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1}) {using var request=Preflight("POST");using var unmatched=await f.Clients[node].SendAsync(request);Assert.Contains(unmatched.StatusCode,new[]{HttpStatusCode.NotFound,HttpStatusCode.MethodNotAllowed});using var normal=await f.RequestAsync(node);normal.EnsureSuccessStatusCode();Assert.False(normal.Headers.Contains("Access-Control-Allow-Origin"));}
    }

    [Fact]
    public async Task Cors_ExactMethodRoutes_KeepBusinessOptionsAndUseOriginalPolicy()
    {
        var calls=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref calls);ctx.Response.StatusCode=200;await ctx.Response.WriteAsJsonAsync(new{method=ctx.Request.Method});})};await f.InitializeAsync();await BindAsync(f,age:11,methods:["GET"]);
        foreach(var pair in new[]{("POST",22),("OPTIONS",33)}){using var created=await f.Control.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{f.Control.Environment.Id}/routes",f.Control.RouteBody("/orders",pair.Item1));created.EnsureSuccessStatusCode();var route=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();await BindAsync(f,route,pair.Item2,methods:[pair.Item1]);}
        await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1})foreach(var pair in new[]{("GET",11),("POST",22),("OPTIONS",33)}){using var request=Preflight(pair.Item1);using var response=await f.Clients[node].SendAsync(request);Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);Assert.Equal(pair.Item2.ToString(),response.Headers.GetValues("Access-Control-Max-Age").Single());}
        Assert.Equal(0,calls);
        foreach(var node in new[]{0,1}){using var request=new HttpRequestMessage(HttpMethod.Options,"/orders");request.Headers.Add("X-API-Key",f.Credential);using var response=await f.Clients[node].SendAsync(request);response.EnsureSuccessStatusCode();Assert.Equal("OPTIONS",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("method").GetString());}Assert.Equal(2,calls);
    }

    [Fact]
    public async Task Cors_WildcardMaxAgeZero_AndIpFirstRejection_HaveNoForgedAllowHeaders()
    {
        await using var f=new GatewayFixture();await f.InitializeAsync();await BindAsync(f,age:0,wildcard:true);await f.ApplyBothAsync(await f.PublishAsync());
        using(var request=Preflight(origin:"https://other.example.com")){using var response=await f.Clients[0].SendAsync(request);Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);Assert.Equal("*",response.Headers.GetValues("Access-Control-Allow-Origin").Single());Assert.False(response.Headers.Contains("Access-Control-Max-Age"));Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));}
        await IpFilterPipelineTests.BindAsync(f,"DenyList",["127.0.0.0/8"]);await f.ApplyBothAsync(await f.PublishAsync());
        using var deniedRequest=Preflight();using var denied=await f.Clients[0].SendAsync(deniedRequest);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));using var json=JsonDocument.Parse(await denied.Content.ReadAsStringAsync());Assert.Equal("ip_filter_rejected",json.RootElement.GetProperty("code").GetString());
    }
    [Theory][InlineData(403)][InlineData(429)]
    public async Task Cors_AuthAndRateRejection_KeepAllowedOrigin(int status)
    {
        var hits=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref hits);await next(ctx);})};
        await f.InitializeAsync();await BindAsync(f);
        await using(var db=f.Control.Context())
        {
            if(status==403){var grant=await db.Set<ApplicationApiPermission>().SingleAsync();grant.ValidFrom=DateTimeOffset.UtcNow.AddHours(-2);grant.ExpiresAt=DateTimeOffset.UtcNow.AddHours(-1);}
            else{var rate=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="CorsRate",Type="rate_limit",Config="{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":1,\"windowMs\":600000,\"burst\":1,\"redisFailureMode\":\"Reject\"}"};db.Add(rate);db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=rate.Id,Priority=2});}
            await db.SaveChangesAsync();
        }
        await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1})
        {
            if(status==429){using var first=await f.RequestAsync(node);if(node==0)first.EnsureSuccessStatusCode();}
            using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Add("Origin","https://portal.example.com");request.Headers.Add("X-API-Key",f.Credential);
            using var response=await f.Clients[node].SendAsync(request);Assert.Equal(status,(int)response.StatusCode);Assert.Equal("https://portal.example.com",response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
        Assert.Equal(status==403?0:1,hits);
    }

    private sealed class PreflightHold : IStartupFilter
    {
        public bool Enabled;public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>
        {
            app.Use(async(HttpContext context,RequestDelegate continuation)=>
            {
                if(Enabled&&HttpMethods.IsOptions(context.Request.Method))context.Response.OnStarting(async()=>{Started.TrySetResult();await Release.Task.WaitAsync(context.RequestAborted);});
                await continuation(context);
            });next(app);
        };
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Cors_HeldPreflightPublishOrCancel_RetainsAndReleasesOriginalLease(bool cancel)
    {
        var hold=new PreflightHold();await using var f=new GatewayFixture{ConfigureGateway=b=>b.Services.AddSingleton<IStartupFilter>(hold)};await f.InitializeAsync();var policy=await BindAsync(f,age:11);var original=await f.PublishAsync();await f.ApplyBothAsync(original);
        hold.Enabled=true;using var abort=new CancellationTokenSource();using var request=Preflight();var waiting=f.Clients[0].SendAsync(request,abort.Token);await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using(var db=f.Control.Context()){var saved=await db.Set<Policy>().SingleAsync(p=>p.Id==policy.Id);saved.Config=Config(22);saved.VersionNo++;await db.SaveChangesAsync();}
        var current=await f.PublishAsync();await f.ApplyBothAsync(current);var store=f.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>();using(var old=store.Acquire(original.Envelope.DeploymentSequence))Assert.Equal(original.Envelope.DeploymentSequence,old.Generation.Envelope.DeploymentSequence);
        hold.Enabled=false;
        if(cancel){abort.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await waiting);}else{hold.Release.TrySetResult();using var response=await waiting;Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);Assert.Equal("11",response.Headers.GetValues("Access-Control-Max-Age").Single());Assert.Equal(original.Envelope.DeploymentSequence.ToString(),response.Headers.GetValues("X-WebApi-Deployment-Sequence").Single());}
        var collected=false;for(var i=0;i<100;i++){try{using var old=store.Acquire(original.Envelope.DeploymentSequence);}catch(InvalidOperationException){collected=true;break;}await Task.Delay(10);}Assert.True(collected);
        using var nextRequest=Preflight();using var nextResponse=await f.Clients[0].SendAsync(nextRequest);Assert.Equal("22",nextResponse.Headers.GetValues("Access-Control-Max-Age").Single());
    }

}

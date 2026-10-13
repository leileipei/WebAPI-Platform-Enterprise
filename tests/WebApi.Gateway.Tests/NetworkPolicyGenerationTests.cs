using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Gateway.Policies;
using Yarp.ReverseProxy.Configuration;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class NetworkPolicyGenerationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RoutingLease_RealProxyShortCircuit204OrCancel_ReclaimsRetiredGeneration(bool cancel)
    {
        var env=Guid.NewGuid();var cluster=Guid.NewGuid();var route=new RuntimeRoute(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),cluster,"/lease",["GET"],0,30000,false);
        var snapshot=new RuntimeSnapshot("2.0",env,1,DateTimeOffset.UtcNow,[route],[new(cluster,env,"RoundRobin",false,"/health",30,[new(Guid.NewGuid(),"http://127.0.0.1:1/",1)])],[],[]);
        using var store=new RuntimeGenerationStore();var original=new RuntimeGeneration(new(Guid.NewGuid(),1,1,new string('a',64),0),[],snapshot);store.Register(original);store.Activate(1);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var b=WebApplication.CreateBuilder();b.WebHost.UseUrls("http://127.0.0.1:0");b.Logging.ClearProviders();b.Services.AddSingleton(store);
        b.Services.AddReverseProxy().LoadFromMemory([new RouteConfig{RouteId=route.Id.ToString(),ClusterId=cluster.ToString(),Match=new RouteMatch{Path="/lease",Methods=["GET"]},Metadata=new Dictionary<string,string>{{"runtimeRouteId",route.Id.ToString()},{"deploymentSequence","1"}}}],
            [new ClusterConfig{ClusterId=cluster.ToString(),Destinations=new Dictionary<string,DestinationConfig>{{"unused",new(){Address="http://127.0.0.1:1/"}}}}]);
        await using var app=b.Build();app.MapReverseProxy(proxy=>{proxy.UseMiddleware<RoutingGenerationMiddleware>();proxy.Use(async(Microsoft.AspNetCore.Http.HttpContext ctx,Microsoft.AspNetCore.Http.RequestDelegate next)=>{started.TrySetResult();await release.Task.WaitAsync(ctx.RequestAborted);ctx.Response.StatusCode=204;});});await app.StartAsync();
        using var client=new HttpClient{BaseAddress=new Uri(GatewayFixture.Url(app))};using var abort=new CancellationTokenSource();var pending=client.GetAsync("/lease",abort.Token);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current=new RuntimeGeneration(original.Envelope with {DeploymentSequence=2},[],snapshot);store.Register(current);store.Activate(2);
        using(var old=store.Acquire(1))Assert.Same(original,old.Generation);
        if(cancel){abort.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await pending);}else{release.TrySetResult();using var response=await pending;Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);}
        var collected=false;for(var i=0;i<100;i++){try{using var old=store.Acquire(1);}catch(InvalidOperationException){collected=true;break;}await Task.Delay(10);}
        Assert.True(collected);Assert.Same(current,store.Current);await app.StopAsync();
    }

    [Fact]
    public void Generation_BodyMappings_AreCompiledOnceAndFrozenWithOriginalConfig()
    {
        const string config="{\"requestHeaders\":[],\"responseHeaders\":[],\"path\":null,\"query\":[],\"requestBody\":{\"methods\":[\"POST\"],\"maxBodyBytes\":1024,\"operations\":[{\"op\":\"add\",\"path\":\"/x\",\"value\":null}]},\"responseBody\":null}";
        var source=Guid.NewGuid();var normalized=PolicyConfigurationValidator.Normalize("transform",config);var id=RuntimePolicyIdentity.Create(source,1,"transform",normalized);var env=Guid.NewGuid();var cluster=Guid.NewGuid();
        var route=new RuntimeRoute(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),cluster,"/body",["POST"],0,30000,true,[new(id,0)]);
        var snapshot=new RuntimeSnapshot("2.3",env,1,DateTimeOffset.UtcNow,[route],[new(cluster,env,"RoundRobin",false,"/health",30,[new(Guid.NewGuid(),"http://127.0.0.1:1/",1)])],[new(id,"transform",normalized,source,1)],[]);
        byte[] bytes=[1,2,3];var generation=new RuntimeGeneration(new(Guid.NewGuid(),1,1,new string('a',64),3),bytes,snapshot);bytes[0]=9;
        Assert.Equal(1,generation.RequestBodyMappings[id].OperationCount);Assert.Empty(generation.ResponseBodyMappings);Assert.Equal(1,generation.Payload.Span[0]);
        var mapping=generation.RequestBodyMappings[id];Assert.Same(mapping,generation.RequestBodyMappings[id]);
        var result=JsonBodyMapper.Apply(System.Text.Encoding.UTF8.GetBytes("{}"),mapping,1024,default);Assert.Equal("{\"x\":null}",System.Text.Encoding.UTF8.GetString(result.Utf8!));
        Assert.Throws<NotSupportedException>(()=>((IList<WebApi.Contracts.Policies.JsonBodyOperation>)generation.Transforms[id].RequestBody!.Operations).Clear());
    }

    [Fact]
    public async Task Generation_EarlyReject_AndSwitch_ReleaseExactlyOnce()
    {
        await using var f=new GatewayFixture();await f.InitializeAsync();var policy=await IpFilterPipelineTests.BindAsync(f,"DenyList",["127.0.0.0/8"]);var original=await f.PublishAsync();await f.ApplyBothAsync(original);
        foreach(var node in new[]{0,1}){using var denied=await f.RequestAsync(node,key:"invalid.invalid");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);}
        var store=f.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>();using var held=store.Acquire(original.Envelope.DeploymentSequence);
        await using(var db=f.Control.Context()){var saved=await db.Set<Policy>().SingleAsync(p=>p.Id==policy.Id);saved.Config="{\"mode\":\"AllowList\",\"ranges\":[\"127.0.0.1\"]}";saved.VersionNo++;await db.SaveChangesAsync();}
        var next=await f.PublishAsync();await f.ApplyBothAsync(next);Assert.Equal(original.Envelope.DeploymentSequence,held.Generation.Envelope.DeploymentSequence);
        using(var old=store.Acquire(original.Envelope.DeploymentSequence))Assert.Same(held.Generation,old.Generation);
        held.Dispose();held.Dispose();Assert.Throws<InvalidOperationException>(()=>store.Acquire(original.Envelope.DeploymentSequence));
        foreach(var node in new[]{0,1}){using var allowed=await f.RequestAsync(node);allowed.EnsureSuccessStatusCode();using var unauthorized=await f.RequestAsync(node,key:"invalid.invalid");Assert.Equal(HttpStatusCode.Unauthorized,unauthorized.StatusCode);}
    }
}

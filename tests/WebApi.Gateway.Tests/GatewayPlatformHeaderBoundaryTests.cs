using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Domain.Policies;
using WebApi.Gateway.Tests.Support;
using SigningKey=WebApi.Gateway.Tests.JwtTokenVerifierTests.KeyFixture;
namespace WebApi.Gateway.Tests;
public sealed class GatewayPlatformHeaderBoundaryTests
{
    internal static void AssertPlatformResponse(HttpResponseMessage response)
    {
        var keys=response.Headers.Where(x=>x.Key.StartsWith("X-WebApi-",StringComparison.OrdinalIgnoreCase)).Select(x=>x.Key.ToLowerInvariant()).Order().ToArray();
        Assert.Equal(new[]{"x-webapi-deployment-sequence","x-webapi-trace-id"},keys);
        Assert.NotEqual("forged",response.Headers.GetValues("X-WebApi-Trace-Id").Single());Assert.NotEqual("999",response.Headers.GetValues("X-WebApi-Deployment-Sequence").Single());
    }
    [Theory][InlineData("ApiKey")][InlineData("Anonymous")][InlineData("JWT")]
    public async Task IncomingAndUpstreamPlatformHeadersCannotSupplyIdentity(string mode)
    {
        using var key=new SigningKey();await using var f=new GatewayFixture{ConfigureBackendA=app=>app.Use(async(ctx,next)=>{
            if(ctx.Request.Path=="/health"){await next();return;}ctx.Response.Headers["X-WebApi-Fake"]="forged";ctx.Response.Headers["X-WebApi-Trace-Id"]="forged";ctx.Response.Headers["X-WebApi-Deployment-Sequence"]="999";
            await ctx.Response.WriteAsJsonAsync(new{headers=ctx.Request.Headers.ToDictionary(x=>x.Key,x=>x.Value.ToString())});
        })};await f.InitializeAsync();
        await using(var db=f.Control.Context())
        {
            db.RemoveRange(await db.Set<RoutePolicyBinding>().Where(x=>x.RouteId==f.RouteId).ToArrayAsync());
            var config=mode=="JWT"?PolicyConfigurationValidator.Normalize("authentication",JsonSerializer.Serialize(key.Configuration() with{ApplicationMappings=[new("private-app-canary-8362",f.Control.Application.Id)]},new JsonSerializerOptions(JsonSerializerDefaults.Web))):JsonSerializer.Serialize(new{mode});
            var policy=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="boundary",Type="authentication",Config=config};db.Add(policy);db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=policy.Id});(await db.Set<ApiRoute>().SingleAsync()).Revision++;await db.SaveChangesAsync();
        }
        await f.ApplyBothAsync(await f.PublishAsync());
        for(var node=0;node<2;node++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Add("X-API-Key",f.Credential);
            foreach(var name in new[]{"x-webapi-fake","X-WebApi-Application-Id","X-WebApi-Subject","X-WebApi-Trace-Id","X-WebApi-Deployment-Sequence"})request.Headers.Add(name,new[]{"forged","999"});
            request.Headers.Authorization=new("Bearer",mode=="JWT"?CachePipelineFixture.Token(key):"business-token");
            using var response=await f.Clients[node].SendAsync(request);response.EnsureSuccessStatusCode();AssertPlatformResponse(response);
            var headers=(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headers").EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetString(),StringComparer.OrdinalIgnoreCase);
            Assert.False(headers.ContainsKey("X-API-Key"));Assert.False(headers.ContainsKey("X-WebApi-Fake"));Assert.False(headers.ContainsKey("X-WebApi-Subject"));Assert.False(headers.ContainsKey("X-WebApi-Application-Id"));Assert.Equal("1",headers["X-WebApi-Deployment-Sequence"]);Assert.NotEqual("forged",headers["X-WebApi-Trace-Id"]);
            if(mode!="JWT")Assert.Equal("Bearer business-token",headers["Authorization"]);
        }
        if(mode=="ApiKey")
        {using var rejected=await f.Clients[0].GetAsync("/orders");Assert.Equal(HttpStatusCode.Unauthorized,rejected.StatusCode);AssertPlatformResponse(rejected);}
    }
}

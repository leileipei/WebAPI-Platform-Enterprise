using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using WebApi.Gateway.Tests.Support;
using WebApi.Gateway.Policies;
using WebApi.Domain.Policies;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class IpFilterPipelineTests
{
    internal static async Task<Policy> BindAsync(GatewayFixture f,string mode,string[] ranges,string auth="ApiKey",string? authConfig=null)
    {
        await using var db=f.Control.Context();var old=await (from b in db.Set<RoutePolicyBinding>() join p in db.Set<Policy>() on b.PolicyId equals p.Id where b.RouteId==f.RouteId&&p.Type=="authentication" select b).ToArrayAsync();db.RemoveRange(old);
        var authentication=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="network auth",Type="authentication",Config=authConfig??JsonSerializer.Serialize(new {mode=auth})};
        var policy=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="IP filter",Type="ip_filter",Config=JsonSerializer.Serialize(new {mode,ranges})};db.AddRange(authentication,policy);db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=authentication.Id});db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=policy.Id,Priority=1});(await db.Set<ApiRoute>().SingleAsync(r=>r.Id==f.RouteId)).Revision++;await db.SaveChangesAsync();return policy;
    }
    [Theory][InlineData("ApiKey")][InlineData("Anonymous")]
    public async Task IpFilter_DeniedBeforeAuthentication_NeverTouchesBackend(string mode)
    {
        var hits=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(ctx,next)=>{Interlocked.Increment(ref hits);await next();})};await f.InitializeAsync();await BindAsync(f,"DenyList",["127.0.0.0/8"],mode);await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1}){using var response=await f.RequestAsync(node,key:"invalid.invalid");Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("ip_filter_rejected",json.RootElement.GetProperty("code").GetString());Assert.Equal("ip_denied",json.RootElement.GetProperty("reason").GetString());}Assert.Equal(0,hits);
    }
    [Theory][InlineData(false,403)][InlineData(true,200)]
    public async Task IpFilter_UsesResolvedRemote_NotSpoofedHeader(bool trusted,int expected)
    {
        var hits=0;await using var f=new GatewayFixture{ConfigureGateway=b=>{if(trusted)b.Configuration["HttpSecurity:TrustedProxyIps:0"]="127.0.0.1";},ConfigureBackendA=a=>a.Use(async(ctx,next)=>{Interlocked.Increment(ref hits);await next();})};
        await f.InitializeAsync();await BindAsync(f,"AllowList",["192.0.2.0/24"]);await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1}){using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Add("X-API-Key",f.Credential);request.Headers.Add("X-Forwarded-For","192.0.2.8");using var response=await f.Clients[node].SendAsync(request);Assert.Equal(expected,(int)response.StatusCode);}Assert.Equal(trusted?2:0,hits);
    }
    [Fact]
    public async Task IpFilter_JwtRejectsBeforeTokenVerification_WithZeroBackendCalls()
    {
        using var key=new JwtTokenVerifierTests.KeyFixture();var hits=0;
        await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(ctx,next)=>{Interlocked.Increment(ref hits);await next();})};await f.InitializeAsync();
        var config=JsonSerializer.Serialize(key.Configuration() with {ApplicationMappings=[new("private-app-canary-8362",f.Control.Application.Id)]},CanonicalJson.Options);
        await BindAsync(f,"DenyList",["127.0.0.0/8"],"JWT",config);await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1}){using var denied=await f.RequestBearerAsync("invalid",node);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);}Assert.Equal(0,hits);
    }
    [Fact]
    public async Task IpFilter_TrustedProxyResolvedIpv6_UsesIpv6Network()
    {
        await using var f=new GatewayFixture{ConfigureGateway=b=>b.Configuration["HttpSecurity:TrustedProxyIps:0"]="127.0.0.1"};await f.InitializeAsync();await BindAsync(f,"AllowList",["2001:db8::/32"]);await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1}) {using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Add("X-API-Key",f.Credential);request.Headers.Add("X-Forwarded-For","2001:db8::1234");using var response=await f.Clients[node].SendAsync(request);response.EnsureSuccessStatusCode();}
    }
    [Theory]
    [InlineData("AllowList","192.0.2.0/24","192.0.2.255",true,null)]
    [InlineData("AllowList","192.0.2.0/24","192.0.3.0",false,"ip_not_allowed")]
    [InlineData("DenyList","2001:db8::/32","2001:db8::1",false,"ip_denied")]
    [InlineData("AllowList","192.0.2.0/24","::ffff:192.0.2.8",true,null)]
    public void CompiledIpFilter_UsesFamilyAndPrefixBits(string mode,string range,string address,bool allowed,string? reason)
    {
        var filter=new CompiledIpFilter(PolicyConfigurationValidator.ParseIpFilter(JsonSerializer.Serialize(new {mode,ranges=new[]{range}})));
        var result=filter.Evaluate(IPAddress.Parse(address));Assert.Equal(allowed,result.Allowed);Assert.Equal(reason,result.Reason);
        var missing=filter.Evaluate(null);Assert.False(missing.Allowed);Assert.Equal("client_ip_unavailable",missing.Reason);
    }
    [Fact]
    public void CompiledIpFilter_ExplicitWholeFamilyAndEmptyDenyList_HaveDefinedBehavior()
    {
        var all=new CompiledIpFilter(PolicyConfigurationValidator.ParseIpFilter("{\"mode\":\"AllowList\",\"ranges\":[\"0.0.0.0/0\"]}"));Assert.True(all.Evaluate(IPAddress.Parse("198.51.100.255")).Allowed);Assert.False(all.Evaluate(IPAddress.IPv6Loopback).Allowed);
        var empty=new CompiledIpFilter(PolicyConfigurationValidator.ParseIpFilter("{\"mode\":\"DenyList\",\"ranges\":[]}"));Assert.True(empty.Evaluate(IPAddress.IPv6Loopback).Allowed);Assert.False(empty.Evaluate(null).Allowed);
    }
    [Theory][InlineData("127.0.0.1")][InlineData("::ffff:127.0.0.1/128")]
    public async Task IpFilter_LoopbackAndMappedRanges_AllowOriginalGrant(string range)
    {
        await using var f=new GatewayFixture();await f.InitializeAsync();await BindAsync(f,"AllowList",[range]);await f.ApplyBothAsync(await f.PublishAsync());foreach(var node in new[]{0,1}){using var response=await f.RequestAsync(node);response.EnsureSuccessStatusCode();}
    }
}

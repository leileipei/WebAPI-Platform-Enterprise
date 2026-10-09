using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.HttpSecurity;
namespace WebApi.Integration.Tests;
public sealed class TrustedProxyBoundaryTests
{
    [Theory][InlineData(null,"192.0.2.12","127.0.0.1")][InlineData("127.0.0.1","192.0.2.12","192.0.2.12")][InlineData("192.0.2.1","192.0.2.12","127.0.0.1")][InlineData("127.0.0.1","192.0.2.12, 203.0.113.3","203.0.113.3")][InlineData("127.0.0.1","not-an-ip","127.0.0.1")][InlineData("127.0.0.1","192.0.2.12, 127.0.0.1, 127.0.0.1, 127.0.0.1, 127.0.0.1, 127.0.0.1","127.0.0.1")]
    public async Task SocketBoundaryUsesOnlyExplicitPeersAndRemovesIncomingHeaders(string? trusted,string forwarded,string expected)
    {
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();
        if(trusted is not null)builder.Configuration["HttpSecurity:TrustedProxyIps:0"]=trusted;
        builder.Services.AddTrustedProxyBoundary(builder.Configuration);
        await using var app=builder.Build();app.UseTrustedProxyBoundary();
        app.MapGet("/",(Microsoft.AspNetCore.Http.HttpContext ctx)=>new{ip=ctx.Connection.RemoteIpAddress!.MapToIPv4().ToString(),host=ctx.Request.Host.Value,scheme=ctx.Request.Scheme,headers=ctx.Request.Headers.Keys.ToArray()});
        await app.StartAsync();var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client=new HttpClient();using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.Add("X-Forwarded-For",forwarded);request.Headers.Add("X-Forwarded-Host","attacker.example");request.Headers.Add("X-Forwarded-Proto","https");request.Headers.Add("Forwarded","for=attacker");request.Headers.Add("X-Original-For","attacker");
        using var response=await client.SendAsync(request);response.EnsureSuccessStatusCode();var body=await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected,body.GetProperty("ip").GetString());Assert.Equal("http",body.GetProperty("scheme").GetString());Assert.DoesNotContain("attacker",body.GetProperty("host").GetString());
        Assert.DoesNotContain(body.GetProperty("headers").EnumerateArray(),x=>x.GetString()!.StartsWith("X-Forwarded-",StringComparison.OrdinalIgnoreCase)||x.GetString()!.StartsWith("X-Original-",StringComparison.OrdinalIgnoreCase)||x.GetString()=="Forwarded");
    }
    [Theory][InlineData("0.0.0.0/0")][InlineData("::/0")][InlineData("10.0.0.0/8")][InlineData("172.16.0.0/12")][InlineData("192.168.0.0/16")][InlineData("invalid")]
    public void BroadOrInvalidNetworksFailClosed(string network)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"HttpSecurity:TrustedProxyNetworks:0",network}}).Build();
        Assert.Throws<InvalidOperationException>(()=>TrustedProxySettings.Read(config));
    }
}

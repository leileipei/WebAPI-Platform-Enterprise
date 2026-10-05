using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Sso;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class SsoMetadataTests
{
    private const string Issuer="https://id.example.test/realm";
    private sealed class Env(string name):IHostEnvironment {public string EnvironmentName{get;set;}=name;public string ApplicationName{get;set;}="tests";public string ContentRootPath{get;set;}="/tmp";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();}
    private sealed class Dns(params string[] addresses):ISsoDnsResolver {public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct)=>Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());}
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(respond(request));}
    private sealed class Stall:HttpMessageHandler {protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){await Task.Delay(Timeout.InfiniteTimeSpan,ct);throw new InvalidOperationException();}}
    private sealed class RebindingDns:ISsoDnsResolver
    {
        public int Calls {get;private set;}
        public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct)=>Task.FromResult(new[]{IPAddress.Parse(++Calls==1?"127.0.0.1":"169.254.169.254")});
    }
    private static SsoOptions Config()=>new(){AllowedOrigins=["https://id.example.test"]};
    private static OidcAddressPolicy Policy(SsoOptions? options=null,string env="Production",params string[] addresses)=>new(Options.Create(options??Config()),new Env(env),new Dns(addresses.Length==0?["8.8.8.8"]:addresses));
    private static string Metadata(string issuer=Issuer,string jwks="https://id.example.test/keys")=>JsonSerializer.Serialize(new{issuer,authorization_endpoint="https://id.example.test/authorize",token_endpoint="https://id.example.test/token",jwks_uri=jwks,response_types_supported=new[]{"code"},code_challenge_methods_supported=new[]{"S256"},token_endpoint_auth_methods_supported=new[]{"client_secret_post"},id_token_signing_alg_values_supported=new[]{"RS256"}});
    private static string Keys(){using var rsa=RSA.Create(2048);var p=rsa.ExportParameters(false);string B(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');return JsonSerializer.Serialize(new {keys=new[]{new{kty="RSA",kid="fixture",use="sig",alg="RS256",n=B(p.Modulus!),e=B(p.Exponent!)}}});}
    [Fact] public async Task LoadsOnlyValidatedDiscoveryAndPublicSigningKeys()
    {
        var keys=Keys();var client=new HttpClient(new Handler(r=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(r.RequestUri!.AbsolutePath.EndsWith("/keys")?keys:Metadata())}));
        var service=new OidcMetadataClient(Options.Create(Config()),Policy(),TimeProvider.System,client);
        var result=await service.GetAsync(Issuer);Assert.Equal(Issuer,result.Configuration.Issuer);Assert.Single(result.Configuration.SigningKeys);Assert.True(result.PkceS256Supported);Assert.True(result.ClientSecretPostSupported);
    }
    [Theory][InlineData("127.0.0.1")][InlineData("10.1.2.3")][InlineData("169.254.169.254")][InlineData("::1")][InlineData("::ffff:127.0.0.1")]
    public async Task MetadataRejectsUnapprovedReboundAddress(string address)=>await Assert.ThrowsAsync<ApiException>(()=>Policy(null,"Production",address).ResolveAllowedAsync(new Uri(Issuer)));
    [Fact] public async Task MixedDnsAnswersCannotSelectPrivateAddress()
    {
        await Assert.ThrowsAsync<ApiException>(()=>Policy(null,"Production","8.8.8.8","10.1.2.3").ResolveAllowedAsync(new Uri(Issuer)));
        var cfg=Config();cfg.AllowedPrivateCidrs=["10.1.2.0/24"];Assert.Single(await Policy(cfg,"Production","10.1.2.3").ResolveAllowedAsync(new Uri(Issuer)));
        await Assert.ThrowsAsync<ApiException>(()=>Policy(cfg,"Production","10.1.3.3").ResolveAllowedAsync(new Uri(Issuer)));
    }
    [Fact] public async Task FixtureHttpRequiresExplicitDevelopmentAndOrigin()
    {
        var cfg=new SsoOptions{AllowedOrigins=["http://localhost:8088"],FixtureEnabled=true};
        Assert.Single(await Policy(cfg,"Development","127.0.0.1").ResolveAllowedAsync(new Uri("http://localhost:8088/realm")));
        await Assert.ThrowsAsync<ApiException>(()=>Policy(cfg,"Production","127.0.0.1").ResolveAllowedAsync(new Uri("http://localhost:8088/realm")));
        await Assert.ThrowsAsync<ApiException>(()=>Policy(cfg,"Development","127.0.0.1").ResolveAllowedAsync(new Uri("http://localhost:8089/realm")));
    }
    [Theory][InlineData("issuer")][InlineData("endpoint")][InlineData("redirect")][InlineData("oversized")][InlineData("algorithm")][InlineData("privatekey")]
    public async Task MetadataRejectsUnapprovedRedirectAndInvalidPayload(string failure)
    {
        var keys=Keys();if(failure=="privatekey")keys=keys.Replace("\"kty\":\"RSA\"","\"kty\":\"RSA\",\"d\":\"private\"");
        var handler=new Handler(r=>{
            if(failure=="redirect")return new(HttpStatusCode.Redirect){Headers={Location=new Uri("http://169.254.169.254/")}};
            var body=r.RequestUri!.AbsolutePath.EndsWith("/keys")?keys:failure switch{"issuer"=>Metadata("https://other.example/"),"endpoint"=>Metadata(jwks:"https://other.example/keys"),"oversized"=>new string(' ',256*1024+1),"algorithm"=>Metadata().Replace("RS256","HS256"),_=>Metadata()};
            return new(HttpStatusCode.OK){Content=new StringContent(body)};
        });
        var service=new OidcMetadataClient(Options.Create(Config()),Policy(),TimeProvider.System,new HttpClient(handler));
        var error=await Assert.ThrowsAsync<ApiException>(()=>service.GetAsync(Issuer));Assert.DoesNotContain("private",error.Message);
    }
    [Fact] public async Task ForceRefreshObtainsNewKeysInsteadOfStaleCache()
    {
        var first=Keys();var latest=first.Replace("\"fixture\"","\"rotated\"");var rotate=false;
        var service=new OidcMetadataClient(Options.Create(Config()),Policy(),TimeProvider.System,new HttpClient(new Handler(r=>new(HttpStatusCode.OK){Content=new StringContent(r.RequestUri!.AbsolutePath.EndsWith("/keys")?(rotate?latest:first):Metadata())})));
        Assert.Equal("fixture",(await service.GetAsync(Issuer)).Configuration.SigningKeys.Single().KeyId);rotate=true;
        Assert.Equal("fixture",(await service.GetAsync(Issuer)).Configuration.SigningKeys.Single().KeyId);
        Assert.Equal("rotated",(await service.GetAsync(Issuer,true)).Configuration.SigningKeys.Single().KeyId);
    }
    [Fact] public async Task DiscoveryStallIsCancelledAtTenSeconds()
    {
        var service=new OidcMetadataClient(Options.Create(Config()),Policy(),TimeProvider.System,new HttpClient(new Stall()));
        var stopwatch=Stopwatch.StartNew();
        var error=await Assert.ThrowsAsync<ApiException>(()=>service.GetAsync(Issuer));
        Assert.Equal("sso_endpoint_timeout",error.Code);Assert.InRange(stopwatch.Elapsed.TotalSeconds,9,14);
    }
    [Fact] public async Task ActualSocketConnectionRechecksDnsAndRejectsRebinding()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var origin=$"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var dns=new RebindingDns();var cfg=new SsoOptions{AllowedOrigins=[origin],FixtureEnabled=true};
        using var client=new HttpClient(new SsoBackchannelHandler(new OidcAddressPolicy(Options.Create(cfg),new Env("Development"),dns)));
        var error=await Assert.ThrowsAsync<ApiException>(()=>client.GetAsync(origin+"/metadata"));
        Assert.Equal("sso_endpoint_rejected",error.Code);Assert.Equal(2,dns.Calls);Assert.False(listener.Pending());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ActualTransportRejectsRedirectAndOversizedTokenBody(bool oversized)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var origin=$"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}";
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply=Task.Run(async ()=>{
            using var socket=await listener.AcceptTcpClientAsync(timeout.Token);await using var stream=socket.GetStream();
            var bytes=new byte[4096];var read=await stream.ReadAsync(bytes,timeout.Token);Assert.True(read>0);
            var body=oversized?new string('x',128*1024+1):"";
            var header=oversized?$"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n{body.Length:x}\r\n{body}\r\n0\r\n\r\n":
                "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header),timeout.Token);
        },timeout.Token);
        var cfg=new SsoOptions{AllowedOrigins=[origin],FixtureEnabled=true};
        using var client=new HttpClient(new SsoBackchannelHandler(Policy(cfg,"Development","127.0.0.1")));
        var error=await Assert.ThrowsAsync<ApiException>(()=>client.PostAsync(origin+"/token",new StringContent("fixture")));
        Assert.Equal(oversized?"sso_response_too_large":"sso_endpoint_rejected",error.Code);
        await reply;Assert.False(listener.Pending());
    }
}

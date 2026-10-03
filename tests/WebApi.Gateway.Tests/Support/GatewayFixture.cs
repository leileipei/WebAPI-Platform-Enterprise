using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Runtime;
using WebApi.Gateway;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Workers;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Integration.Tests.Support;
using WebApi.TestBackend;
namespace WebApi.Gateway.Tests.Support;
public sealed class GatewayFixture : IAsyncDisposable
{
    public ApiFixture Control {get;}=new();public WebApplication BackendA=null!,BackendB=null!;public WebApplication[] Gateways=new WebApplication[2];public HttpClient[] Clients=new HttpClient[2];public string Directory {get;}=Path.Combine(Path.GetTempPath(),"gateway-test-"+Guid.NewGuid());public string Credential="";public string[] BackendUrls=new string[2];public Guid RouteId;private int version=1;private readonly List<string> secrets=[];
    public Action<WebApplicationBuilder>? ConfigureGateway {get;set;}
    private Guid credentialId;
    public static string Url(WebApplication app)=>app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    public async Task InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(Directory);
        BackendA=TestBackendApp.Build([],b=>{b.WebHost.UseUrls("http://127.0.0.1:0");b.Configuration["Backend:Id"]="A";b.Logging.ClearProviders();});BackendB=TestBackendApp.Build([],b=>{b.WebHost.UseUrls("http://127.0.0.1:0");b.Configuration["Backend:Id"]="B";b.Logging.ClearProviders();});await BackendA.StartAsync();await BackendB.StartAsync();BackendUrls=[Url(BackendA),Url(BackendB)];
        for(var i=0;i<2;i++) {var file=Path.Combine(Directory,"node-"+i+".secret");await File.WriteAllTextAsync(file,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));secrets.Add(file);}
        await Control.InitializeAsync(b=>{for(var i=0;i<2;i++) {b.Configuration[$"Nodes:Enrollments:{i}:EnvironmentId"]=Control.Environment.Id.ToString();b.Configuration[$"Nodes:Enrollments:{i}:NodeName"]="gateway-"+i;b.Configuration[$"Nodes:Enrollments:{i}:SecretFile"]=secrets[i];b.Configuration[$"Upstream:AllowedOrigins:{i}"]=BackendUrls[i];}});await Control.SeedReleaseAsync();using var login=await Control.LoginAsync();login.EnsureSuccessStatusCode();
        for(var i=0;i<2;i++) {Gateways[i]=await StartGatewayAsync(i);Clients[i]=new HttpClient {BaseAddress=new Uri(Url(Gateways[i]))};}
        await using(var db=Control.Context()) {var dst=await db.Set<UpstreamDestination>().SingleAsync(d=>d.Id==Control.Destination.Id);dst.Address=BackendUrls[0]+"/";await db.SaveChangesAsync();}
        using var route=await Control.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{Control.Environment.Id}/routes",Control.RouteBody("/orders"));route.EnsureSuccessStatusCode();RouteId=(await route.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var credential=await Control.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{Control.Application.Id}/credentials",new {validFrom=DateTimeOffset.UtcNow.AddMinutes(-1),expiresAt=DateTimeOffset.UtcNow.AddDays(1)});credential.EnsureSuccessStatusCode();var key=await credential.Content.ReadFromJsonAsync<JsonElement>();Credential=key.GetProperty("apiKey").GetString()!;credentialId=key.GetProperty("credential").GetProperty("id").GetGuid();
        using var grant=await Control.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{Control.Application.Id}/permissions",new {environmentId=Control.Environment.Id,apiId=Control.Api.Id,validFrom=DateTimeOffset.UtcNow.AddMinutes(-1),expiresAt=DateTimeOffset.UtcNow.AddDays(1)});grant.EnsureSuccessStatusCode();
    }
    public async Task<WebApplication> StartGatewayAsync(int i,bool register=true)
    {
        var app=GatewayApp.Build([],b=>{b.WebHost.UseUrls("http://127.0.0.1:0");b.Configuration["Gateway:EnvironmentId"]=Control.Environment.Id.ToString();b.Configuration["Gateway:NodeName"]="gateway-"+i;b.Configuration["Gateway:SecretFile"]=secrets[i];b.Configuration["Gateway:LkgDirectory"]=Path.Combine(Directory,"lkg-"+i);b.Configuration["Gateway:ControlPlaneUrl"]=Control.Client.BaseAddress!.ToString();b.Configuration["Gateway:AutomaticUpdates"]="false";for(var j=0;j<2;j++) b.Configuration[$"Upstream:AllowedOrigins:{j}"]=BackendUrls[j];b.Logging.ClearProviders();ConfigureGateway?.Invoke(b);});await app.StartAsync();if(register) await app.Services.GetRequiredService<NodeClient>().RegisterAsync();return app;
    }
    public async Task<DesiredConfigResponse> PublishAsync(int backend=0)
    {
        var id=Control.Version.Id;long baseline;
        await using(var db=Control.Context()) {baseline=(await db.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion??0;}
        if(version>1)
        {using var created=await Control.WriteAsync(HttpMethod.Post,$"/api/v1/apis/{Control.Api.Id}/versions",new {version=version+".0.0",changeType="compatible"});created.EnsureSuccessStatusCode();id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();using var routeCurrent=await Control.Client.GetAsync($"/api/v1/routes/{RouteId}");var routeData=await routeCurrent.Content.ReadFromJsonAsync<JsonElement>();using var updated=await Control.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{RouteId}",new {apiVersionId=id,routeName="Orders",path="/orders",methods=new[]{"GET"},clusterId=Control.Cluster.Id,priority=100,enabled=true,timeoutMs=30000},routeCurrent.Headers.ETag!.ToString());updated.EnsureSuccessStatusCode();}
        await using(var db=Control.Context()) {var dst=await db.Set<UpstreamDestination>().SingleAsync(d=>d.Id==Control.Destination.Id);dst.Address=BackendUrls[backend]+"/";dst.Revision++;await db.SaveChangesAsync();}
        using var release=await ApiFixture.CommandAsync(Control.Client,$"/api/v1/environments/{Control.Environment.Id}/releases",new {baseConfigVersion=baseline,versionIds=new[]{id},resourceRevisions=new[]{new {type="version",id,revision=1L}}});release.EnsureSuccessStatusCode();var releaseId=(await release.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();using var submit=await ApiFixture.CommandAsync(Control.Client,$"/api/v1/releases/{releaseId}/submit");submit.EnsureSuccessStatusCode();foreach(var role in new[]{"ApiApprover","SecurityReviewer"}) {var reviewer=await Control.NewReviewerAsync(role);using var approved=await ApiFixture.CommandAsync(reviewer.Client,$"/api/v1/releases/{releaseId}/approve",new {comment="审核"});approved.EnsureSuccessStatusCode();}using var publish=await ApiFixture.CommandAsync(Control.Client,$"/api/v1/releases/{releaseId}/publish");publish.EnsureSuccessStatusCode();using(var scope=Control.Services()) {await scope.ServiceProvider.GetRequiredService<PublishCoordinator>().BuildNextAsync();}
        await using var check=Control.Context();var config=await check.Set<GatewayConfigVersion>().OrderByDescending(v=>v.VersionNo).FirstAsync();var snapshot=await check.Set<GatewayConfigSnapshot>().SingleAsync(s=>s.ConfigVersionId==config.Id);var r=await check.Set<ReleaseRecord>().SingleAsync(r=>r.Id==releaseId);version++;return new(new(releaseId,r.DeploymentSequence!.Value,config.VersionNo,config.SnapshotHash!,snapshot.SizeBytes),snapshot.PayloadBytes);
    }
    public async Task ApplyBothAsync(DesiredConfigResponse config) {foreach(var gateway in Gateways) {var result=await gateway.Services.GetRequiredService<ConfigWatcher>().AcceptAsync(config);if(!result.Applied) throw new InvalidOperationException("Gateway activation failed: "+result.ErrorCode);}}
    public async Task RotateCredentialAsync()
    {await using(var db=Control.Context()) {var old=await db.Set<ApplicationCredential>().SingleAsync(k=>k.Id==credentialId);old.Status="Revoked";old.Revision++;await db.SaveChangesAsync();}using var created=await Control.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{Control.Application.Id}/credentials",new {validFrom=DateTimeOffset.UtcNow.AddMinutes(-1),expiresAt=DateTimeOffset.UtcNow.AddDays(1)});created.EnsureSuccessStatusCode();var key=await created.Content.ReadFromJsonAsync<JsonElement>();Credential=key.GetProperty("apiKey").GetString()!;credentialId=key.GetProperty("credential").GetProperty("id").GetGuid();}
    public async Task RestartAsync(int i,bool register=true)
    {Clients[i].Dispose();await Gateways[i].StopAsync();await Gateways[i].DisposeAsync();Gateways[i]=await StartGatewayAsync(i,register);Clients[i]=new HttpClient {BaseAddress=new Uri(Url(Gateways[i]))};}
    public Task<HttpResponseMessage> RequestAsync(int i=0,string? key=null,string path="/orders") {var request=new HttpRequestMessage(HttpMethod.Get,path);request.Headers.Add("X-API-Key",key??Credential);return Clients[i].SendAsync(request);}
    public async ValueTask DisposeAsync() {foreach(var client in Clients) client?.Dispose();foreach(var app in Gateways) if(app is not null) {await app.StopAsync();await app.DisposeAsync();}await Control.DisposeAsync();foreach(var app in new[]{BackendA,BackendB}) if(app is not null) {await app.StopAsync();await app.DisposeAsync();}if(System.IO.Directory.Exists(Directory)) {if(!OperatingSystem.IsWindows()) foreach(var dir in System.IO.Directory.EnumerateDirectories(Directory)) File.SetUnixFileMode(dir,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);System.IO.Directory.Delete(Directory,true);}}
}

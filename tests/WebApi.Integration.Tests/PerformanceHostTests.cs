using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.PerformanceHost;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;
public sealed class PerformanceHostTests
{
    [Fact]
    public void ExistingPrivateKeyRejectsExecutableOrSharedPermissions()
    {
        if(OperatingSystem.IsWindows())return;
        var directory=Path.Combine(Path.GetTempPath(),"performance-key-mode-"+Guid.NewGuid().ToString("N"));
        try {
            using(var material=PerformanceJwtMaterial.LoadOrCreate(directory)){}
            foreach(var additional in new[]{UnixFileMode.UserExecute,UnixFileMode.GroupRead,UnixFileMode.OtherRead}) {
                File.SetUnixFileMode(Path.Combine(directory,"rsa.pem"),UnixFileMode.UserRead|UnixFileMode.UserWrite|additional);
                Assert.Throws<InvalidOperationException>(()=>PerformanceJwtMaterial.LoadOrCreate(directory));
            }
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ControlPlaneUsesRealLoginAndRecordsSqlWithoutGrantingProbeTokenBusinessAccess(bool explain)
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();
        var directory=Path.Combine(Path.GetTempPath(),"performance-cp-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try {
            var now=DateTimeOffset.UtcNow;var spec=new DatasetSpec(100,20261009,now.AddMinutes(-1),now.AddHours(20));
            await using(var db=database.Context()){await db.Database.MigrateAsync();await PerformanceDataset.CreateAsync(db,spec,directory,CancellationToken.None);}
            var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllTextAsync(Path.Combine(directory,"probe-token"),token);await File.WriteAllTextAsync(Path.Combine(directory,"login-hmac"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            if(!OperatingSystem.IsWindows())foreach(var name in new[]{"probe-token","login-hmac"})File.SetUnixFileMode(Path.Combine(directory,name),UnixFileMode.UserRead|UnixFileMode.UserWrite);
            var configuration=new Dictionary<string,string?> { ["urls"]="http://127.0.0.1:0",["ConnectionStrings:WebApi"]=database.ConnectionString,["Performance:PrivateDirectory"]=directory,["DataProtection:KeysDirectory"]=Path.Combine(directory,"data-protection"),["DataProtection:ApplicationName"]="performance-test-"+Guid.NewGuid().ToString("N"),["Authentication:LoginProtection:HmacSecretFile"]=Path.Combine(directory,"login-hmac"),["Authentication:LoginProtection:RedisPrefix"]="performance-test-"+Guid.NewGuid().ToString("N"),["Redis:Connection"]=Environment.GetEnvironmentVariable("WEBAPI_TEST_REDIS")??"redis:6379" };
            if(explain){configuration["Performance:ExplainEnabled"]="true";configuration["Performance:DiagnosticMode"]="explain";}
            await using var host=PerformanceHosts.Build("controlplane",configuration);
            await host.StartAsync();using var client=new HttpClient(new HttpClientHandler {CookieContainer=new CookieContainer(),AllowAutoRedirect=false}) {BaseAddress=new Uri(Address(host))};
            var path=$"/api/v1/projects/{PerformanceDataset.ResourceId(spec.Seed,"project",0)}/apis?page=1&pageSize=1";
            using(var denied=new HttpRequestMessage(HttpMethod.Get,path)){denied.Headers.Add("X-Performance-Token",token);Assert.Equal(HttpStatusCode.Unauthorized,(await client.SendAsync(denied)).StatusCode);}
            using var csrfResponse=await client.GetAsync("/api/v1/auth/csrf");var csrf=(await csrfResponse.Content.ReadFromJsonAsync<Dictionary<string,string>>())!["token"];
            using var login=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/login") {Content=JsonContent.Create(new {username="performance_admin",password=(await File.ReadAllTextAsync(Path.Combine(directory,"admin-password"))).Trim()})};login.Headers.Add("X-CSRF-Token",csrf);login.Headers.Add("X-Performance-Token",token);
            using var loggedIn=await client.SendAsync(login);Assert.Equal(HttpStatusCode.OK,loggedIn.StatusCode);
            using var request=new HttpRequestMessage(HttpMethod.Get,path);request.Headers.Add("X-Performance-Token",token);
            using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using var page=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(100,page.RootElement.GetProperty("total").GetInt32());Assert.Equal(1,page.RootElement.GetProperty("items").GetArrayLength());
            var requestId=response.Headers.GetValues("X-Performance-Request-Id").Single();
            using var statsRequest=new HttpRequestMessage(HttpMethod.Get,"/__perf/sql/"+requestId);statsRequest.Headers.Add("X-Performance-Token",token);
            using var statsResponse=await client.SendAsync(statsRequest);Assert.Equal(HttpStatusCode.OK,statsResponse.StatusCode);
            var evidence=await statsResponse.Content.ReadAsStringAsync();using var stats=JsonDocument.Parse(evidence);
            Assert.True(stats.RootElement.GetProperty("stats").GetProperty("total").GetInt32()>0);Assert.True(stats.RootElement.GetProperty("stats").GetProperty("rows").GetInt64()>0);
            Assert.DoesNotContain("PasswordHash",evidence,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("SELECT",evidence,StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/__perf/explain/"+requestId)).StatusCode);
            using var planRequest=new HttpRequestMessage(HttpMethod.Get,"/__perf/explain/"+requestId);planRequest.Headers.Add("X-Performance-Token",token);
            using var planResponse=await client.SendAsync(planRequest);Assert.Equal(explain?HttpStatusCode.OK:HttpStatusCode.NotFound,planResponse.StatusCode);
            if(explain){
                var planJson=await planResponse.Content.ReadAsStringAsync();using var plan=JsonDocument.Parse(planJson);Assert.True(plan.RootElement.GetProperty("explained").GetInt32()>0);
                var hashes=stats.RootElement.GetProperty("shapes").EnumerateArray().Select(s=>s.GetProperty("hash").GetString()).ToHashSet();
                Assert.All(plan.RootElement.GetProperty("queries").EnumerateArray(),q=>Assert.Contains(q.GetProperty("hash").GetString(),hashes));
                Assert.DoesNotContain("SELECT",planJson,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain(token,planJson);Assert.DoesNotContain((await File.ReadAllTextAsync(Path.Combine(directory,"admin-password"))).Trim(),planJson);
            }
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Theory]
    [InlineData("controlplane",null)] [InlineData("controlplane","baseline")] [InlineData("gateway","explain")] [InlineData("backend","explain")]
    public void ExplainCannotBeEnabledInsidePerformanceOrNonControlPlaneHosts(string mode,string? diagnosticMode)
    {
        Assert.Throws<InvalidDataException>(()=>PerformanceHosts.Build(mode,new Dictionary<string,string?>{["Performance:ExplainEnabled"]="true",["Performance:DiagnosticMode"]=diagnosticMode}));
    }
    [Fact]
    public async Task PreparedFixtureHasReusablePrivateRequestsAndNoSecretsInManifest()
    {
        var directory=Path.Combine(Path.GetTempPath(),"performance-files-"+Guid.NewGuid().ToString("N"));
        try {
            var now=DateTimeOffset.UtcNow;var spec=new DatasetSpec(100,20261009,now.AddMinutes(-1),now.AddHours(20));
            var manifest=await PerformanceFixtureFiles.PrepareAsync(spec,directory);
            Assert.Equal(100,manifest.Counts["routes"]);Assert.Equal(200,manifest.Counts["credentials"]);Assert.Equal(5,manifest.SnapshotHashes.Count);
            var publicEvidence=System.Text.Json.JsonSerializer.Serialize(manifest,CanonicalJson.Options);
            Assert.DoesNotContain("Bearer ",publicEvidence);Assert.DoesNotContain("PRIVATE KEY",publicEvidence);Assert.DoesNotContain("ak_00000",publicEvidence);
            using var requests=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"requests.json")));
            var first=requests.RootElement.GetProperty("api-key")[0];Assert.Equal(200,first.GetProperty("expected").GetInt32());Assert.StartsWith("ak_00000.",first.GetProperty("headers").GetProperty("X-API-Key").GetString());
            Assert.Equal(401,requests.RootElement.GetProperty("no-mapping")[0].GetProperty("expected").GetInt32());Assert.Equal(403,requests.RootElement.GetProperty("no-grant")[0].GetProperty("expected").GetInt32());
            if(!OperatingSystem.IsWindows())foreach(var file in Directory.GetFiles(directory))Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(file));
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task TwoRealGatewaysApplyPrivateFixturesAndForwardWithActualAuthentication()
    {
        var directory=Path.Combine(Path.GetTempPath(),"performance-host-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        using var keys=PerformanceJwtMaterial.LoadOrCreate(directory);
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(Path.Combine(directory,"probe-token"),token);await File.WriteAllTextAsync(Path.Combine(directory,"gateway-secret"),token);
        if(!OperatingSystem.IsWindows())foreach(var name in new[]{"probe-token","gateway-secret"})File.SetUnixFileMode(Path.Combine(directory,name),UnixFileMode.UserRead|UnixFileMode.UserWrite);
        var now=DateTimeOffset.UtcNow;var spec=new DatasetSpec(100,20261009,now.AddMinutes(-1),now.AddHours(20));
        await using var backend=PerformanceHosts.Build("backend",new Dictionary<string,string?> { ["urls"]="http://127.0.0.1:0" });await backend.StartAsync();var origin=Address(backend);
        foreach(var (profile,mode,algorithm) in new[]{("anonymous",AuthenticationMode.Anonymous,"RS256"),("apikey",AuthenticationMode.ApiKey,"RS256"),("rs256",AuthenticationMode.JWT,"RS256"),("es256",AuthenticationMode.JWT,"ES256")}) {
            var snapshot=PerformanceDataset.BuildSnapshot(spec,mode,algorithm,keys);
            snapshot=snapshot with {Clusters=snapshot.Clusters.Select(c=>c with {Destinations=c.Destinations.Select(d=>d with {Address=origin+"/"}).ToArray()}).ToArray()};
            await File.WriteAllBytesAsync(Path.Combine(directory,profile+".json"),CanonicalJson.Serialize(snapshot));
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(Path.Combine(directory,profile+".json"),UnixFileMode.UserRead|UnixFileMode.UserWrite);
        }
        Dictionary<string,string?> Config(string name)=>new() { ["urls"]="http://127.0.0.1:0",["Performance:PrivateDirectory"]=directory,["Gateway:EnvironmentId"]=PerformanceDataset.ResourceId(spec.Seed,"environment",0).ToString(),["Gateway:NodeName"]=name,["Gateway:SecretFile"]=Path.Combine(directory,"gateway-secret"),["Gateway:LkgDirectory"]=Path.Combine(directory,name),["Gateway:ControlPlaneUrl"]="http://127.0.0.1:1",["Gateway:AutomaticUpdates"]="false",["Upstream:AllowedOrigins:0"]=origin,["Observability:Enabled"]="false" };
        try {
            await using var a=PerformanceHosts.Build("gateway",Config("gateway-a"));await using var b=PerformanceHosts.Build("gateway",Config("gateway-b"));await a.StartAsync();await b.StartAsync();
            foreach(var host in new[]{a,b}) {
                using var client=new HttpClient {BaseAddress=new Uri(Address(host))};
                Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.GetAsync("/health/ready")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/__perf/state")).StatusCode);
                using var stateRequest=new HttpRequestMessage(HttpMethod.Get,"/__perf/state");stateRequest.Headers.Add("X-Performance-Token",token);
                using var stateResponse=await client.SendAsync(stateRequest);Assert.Equal(HttpStatusCode.OK,stateResponse.StatusCode);
                using var state=JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync());
                Assert.True(state.RootElement.GetProperty("totalAvailableMemoryBytes").GetInt64()>0);
                long sequence=0;
                foreach(var profile in new[]{"anonymous","apikey","rs256","es256"}) {
                    using var apply=new HttpRequestMessage(HttpMethod.Post,"/__perf/apply") {Content=JsonContent.Create(new {profile,sequence=++sequence})};apply.Headers.Add("X-Performance-Token",token);
                    using var applied=await client.SendAsync(apply);Assert.True(applied.StatusCode==HttpStatusCode.OK,$"{profile}: {applied.StatusCode}, {await applied.Content.ReadAsStringAsync()}");
                    using var request=new HttpRequestMessage(HttpMethod.Get,"/r/0");
                    if(profile=="apikey")request.Headers.Add("X-API-Key","ak_00000."+keys.CredentialSecret(0));
                    if(profile is "rs256" or "es256")request.Headers.Add("Authorization","Bearer "+keys.Token(profile.ToUpperInvariant(),"app00000",spec.WindowStart,spec.WindowEnd));
                    using var forwarded=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,forwarded.StatusCode);Assert.Equal(1024,(await forwarded.Content.ReadAsByteArrayAsync()).Length);
                    if(profile!="anonymous")Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/r/0")).StatusCode);
                    if(profile is "rs256" or "es256") {
                        using var noMapping=new HttpRequestMessage(HttpMethod.Get,"/r/0");noMapping.Headers.Add("Authorization","Bearer "+keys.Token(profile.ToUpperInvariant(),"unknown-app",spec.WindowStart,spec.WindowEnd));
                        Assert.Equal(HttpStatusCode.Unauthorized,(await client.SendAsync(noMapping)).StatusCode);
                        using var noGrant=new HttpRequestMessage(HttpMethod.Get,"/r/9");noGrant.Headers.Add("Authorization","Bearer "+keys.Token(profile.ToUpperInvariant(),"app00000",spec.WindowStart,spec.WindowEnd));
                        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(noGrant)).StatusCode);
                    }
                }
                using var traversal=new HttpRequestMessage(HttpMethod.Post,"/__perf/apply") {Content=JsonContent.Create(new {profile="../probe-token",sequence=99})};traversal.Headers.Add("X-Performance-Token",token);
                Assert.Equal(HttpStatusCode.BadRequest,(await client.SendAsync(traversal)).StatusCode);
                const string privateCanary="activation-private-canary";
                await File.WriteAllTextAsync(Path.Combine(directory,"anonymous.json"),"{\""+privateCanary+"\":");
                using var malformed=new HttpRequestMessage(HttpMethod.Post,"/__perf/apply") {Content=JsonContent.Create(new {profile="anonymous",sequence=100})};malformed.Headers.Add("X-Performance-Token",token);
                using var rejected=await client.SendAsync(malformed);Assert.Equal(HttpStatusCode.BadRequest,rejected.StatusCode);
                var diagnosticBody=await rejected.Content.ReadAsStringAsync();Assert.Contains("snapshot-parse",diagnosticBody);Assert.Contains("ApiException",diagnosticBody);Assert.DoesNotContain(privateCanary,diagnosticBody);Assert.DoesNotContain(token,diagnosticBody);
                var restored=PerformanceDataset.BuildSnapshot(spec,AuthenticationMode.Anonymous,"RS256",keys);
                restored=restored with {Clusters=restored.Clusters.Select(c=>c with {Destinations=c.Destinations.Select(d=>d with {Address=origin+"/"}).ToArray()}).ToArray()};await File.WriteAllBytesAsync(Path.Combine(directory,"anonymous.json"),CanonicalJson.Serialize(restored));
                var diagnosticFiles=Directory.GetFiles(directory,"activation-failure-*.json");Assert.NotEmpty(diagnosticFiles);
                var failedBytes=System.Text.Encoding.UTF8.GetBytes("{\""+privateCanary+"\":");
                Assert.All(diagnosticFiles,file=>{
                    using var diagnostic=JsonDocument.Parse(File.ReadAllText(file));var root=diagnostic.RootElement;
                    Assert.Equal("snapshot-parse",root.GetProperty("stage").GetString());Assert.Equal("ApiException",root.GetProperty("errorType").GetString());Assert.NotEmpty(root.GetProperty("exception").GetString()!);
                    var payloadName=root.GetProperty("payloadFile").GetString()!;
                    Assert.Equal(Path.GetFileName(file).Replace(".json",".bin"),payloadName);
                    var payloadFile=Path.Combine(directory,payloadName);
                    // The source has already been restored. Retain the bytes consumed by
                    // the failed request, rather than re-reading the current fixture file.
                    Assert.Equal(failedBytes,File.ReadAllBytes(payloadFile));
                    Assert.Equal(failedBytes.Length,root.GetProperty("payloadBytes").GetInt32());
                    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(failedBytes)),root.GetProperty("payloadSha256").GetString());
                    Assert.DoesNotContain(payloadName,diagnosticBody);
                    if(!OperatingSystem.IsWindows())foreach(var retained in new[]{file,payloadFile})Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(retained));
                });
            }
        }finally{Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task MissingPrivateFixtureRetainsFailureWithoutInventingPayload()
    {
        var directory=Path.Combine(Path.GetTempPath(),"performance-host-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        foreach(var name in new[]{"probe-token","gateway-secret"}) {
            var file=Path.Combine(directory,name);await File.WriteAllTextAsync(file,token);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        }
        try {
            await using var host=PerformanceHosts.Build("gateway",new Dictionary<string,string?> {
                ["urls"]="http://127.0.0.1:0",["Performance:PrivateDirectory"]=directory,
                ["Gateway:EnvironmentId"]=Guid.NewGuid().ToString(),["Gateway:NodeName"]="missing-fixture",
                ["Gateway:SecretFile"]=Path.Combine(directory,"gateway-secret"),["Gateway:LkgDirectory"]=Path.Combine(directory,"lkg"),
                ["Gateway:ControlPlaneUrl"]="http://127.0.0.1:1",["Gateway:AutomaticUpdates"]="false",["Observability:Enabled"]="false"
            });
            await host.StartAsync();using var client=new HttpClient {BaseAddress=new Uri(Address(host))};
            using var request=new HttpRequestMessage(HttpMethod.Post,"/__perf/apply") {Content=JsonContent.Create(new {profile="anonymous",sequence=1})};request.Headers.Add("X-Performance-Token",token);
            using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
            using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.True(body.RootElement.GetProperty("diagnosticRetained").GetBoolean());
            Assert.Equal("file-check",body.RootElement.GetProperty("stage").GetString());Assert.False(body.RootElement.TryGetProperty("payloadFile",out _));
            using var retained=JsonDocument.Parse(File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory,"activation-failure-*.json"))));
            foreach(var property in new[]{"payloadFile","payloadBytes","payloadSha256"})Assert.Equal(JsonValueKind.Null,retained.RootElement.GetProperty(property).ValueKind);
            Assert.Empty(Directory.GetFiles(directory,"activation-failure-*.bin"));
        }finally{Directory.Delete(directory,true);}
    }
    private static string Address(Microsoft.AspNetCore.Builder.WebApplication app)=>app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
}

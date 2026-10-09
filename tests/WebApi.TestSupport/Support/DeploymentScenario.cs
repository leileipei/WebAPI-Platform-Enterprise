using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
namespace WebApi.Integration.Tests.Support;
public sealed class DeploymentScenario : IAsyncDisposable
{
    public ApiFixture Api {get;}=new();public Guid[] NodeIds=new Guid[2];public Guid[] Instances=[Guid.NewGuid(),Guid.NewGuid()];private string[] secrets=[Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))];private string[] files=[Path.GetTempFileName(),Path.GetTempFileName()];private Guid routeId;private int version=1;
    public async Task InitializeAsync(Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure=null)
    {
        for(var i=0;i<2;i++) {await File.WriteAllTextAsync(files[i],secrets[i]);if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(files[i],UnixFileMode.UserRead|UnixFileMode.UserWrite);}
        await Api.InitializeAsync(b=>{for(var i=0;i<2;i++) {b.Configuration[$"Nodes:Enrollments:{i}:EnvironmentId"]=Api.Environment.Id.ToString();b.Configuration[$"Nodes:Enrollments:{i}:NodeName"]="node-"+i;b.Configuration[$"Nodes:Enrollments:{i}:SecretFile"]=files[i];}configure?.Invoke(b);});await Api.SeedReleaseAsync();using var login=await Api.LoginAsync();login.EnsureSuccessStatusCode();
        await using(var db=Api.Context()) {var roleId=await db.Set<UserRole>().Where(r=>r.UserId==Api.User.Id).Select(r=>r.RoleId).SingleAsync();foreach(var code in new[]{"gateway.read","gateway.operate","gateway.config.read"}) {var p=new Permission {Code=code,Module="gateway",Name=code};db.Add(p);db.Add(new RolePermission {RoleId=roleId,PermissionId=p.Id});}await db.SaveChangesAsync();}
        for(var i=0;i<2;i++) {using var registered=await NodeAsync(i,"/internal/v1/nodes/register",new RegisterNodeRequest(Api.Environment.Id,"node-"+i,Instances[i],"test"));registered.EnsureSuccessStatusCode();NodeIds[i]=(await registered.Content.ReadFromJsonAsync<RegisteredNode>())!.NodeId;}
        using var route=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{Api.Environment.Id}/routes",Api.RouteBody("/orders"));route.EnsureSuccessStatusCode();routeId=(await route.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var credential=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{Api.Application.Id}/credentials",new {validFrom=DateTimeOffset.UtcNow.AddMinutes(-1),expiresAt=DateTimeOffset.UtcNow.AddDays(1)});credential.EnsureSuccessStatusCode();using var grant=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/applications/{Api.Application.Id}/permissions",new {environmentId=Api.Environment.Id,apiId=Api.Api.Id,validFrom=DateTimeOffset.UtcNow.AddMinutes(-1),expiresAt=DateTimeOffset.UtcNow.AddDays(1)});grant.EnsureSuccessStatusCode();
    }
    public async Task ApproveAsync(Guid id)
    {foreach(var role in new[]{"ApiApprover","SecurityReviewer"}) {var reviewer=await Api.NewReviewerAsync(role);using var result=await ApiFixture.CommandAsync(reviewer.Client,$"/api/v1/releases/{id}/approve",new {comment="独立审核"});result.EnsureSuccessStatusCode();}}
    public async Task<Guid> ReadyAsync()
    {
        var id=Api.Version.Id;if(version>1) {using var created=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/apis/{Api.Api.Id}/versions",new {version=version+".0.0"});created.EnsureSuccessStatusCode();id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();using var current=await Api.Client.GetAsync($"/api/v1/routes/{routeId}");var currentRoute=await current.Content.ReadFromJsonAsync<JsonElement>();using var updated=await Api.WriteAsync(HttpMethod.Put,$"/api/v1/routes/{routeId}",new {apiVersionId=id,routeName="orders",path="/orders",methods=new[]{"GET"},clusterId=Api.Cluster.Id,timeoutMs=currentRoute.GetProperty("timeoutMs").GetInt32()},current.Headers.ETag!.ToString());updated.EnsureSuccessStatusCode();await using var change=Api.Context();var dst=await change.Set<UpstreamDestination>().SingleAsync();dst.Address="http://test-backend:8080/v"+version+"/";dst.Revision++;await change.SaveChangesAsync();}
        long baseline;await using(var db=Api.Context()) baseline=(await db.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion??0;
        using var create=await ApiFixture.CommandAsync(Api.Client,$"/api/v1/environments/{Api.Environment.Id}/releases",await Api.PreviewReleaseRequestAsync(baseline,id));create.EnsureSuccessStatusCode();var releaseId=(await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();using var submit=await ApiFixture.CommandAsync(Api.Client,$"/api/v1/releases/{releaseId}/submit");submit.EnsureSuccessStatusCode();await ApproveAsync(releaseId);version++;return releaseId;
    }
    public async Task BuildAsync(Guid id)
    {using var publish=await ApiFixture.CommandAsync(Api.Client,$"/api/v1/releases/{id}/publish");publish.EnsureSuccessStatusCode();await BuildQueuedAsync();}
    public async Task BuildQueuedAsync() {using var scope=Api.Services();if(!await scope.ServiceProvider.GetRequiredService<PublishCoordinator>().BuildNextAsync()) throw new InvalidOperationException("No queued release.");}
    public async Task<DesiredConfigResponse> DesiredAsync(Guid releaseId)
    {await using var db=Api.Context();var r=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==releaseId);var v=await db.Set<GatewayConfigVersion>().SingleAsync(v=>v.EnvironmentId==r.EnvironmentId&&v.VersionNo==r.ToConfigVersion);var s=await db.Set<GatewayConfigSnapshot>().SingleAsync(s=>s.ConfigVersionId==v.Id);return new(new(r.Id,r.DeploymentSequence!.Value,v.VersionNo,v.SnapshotHash!,s.SizeBytes),s.PayloadBytes);}
    public async Task AckAllAsync(Guid id)
    {var desired=await DesiredAsync(id);for(var i=0;i<2;i++) {using var ack=await NodeAsync(i,$"/internal/v1/nodes/{NodeIds[i]}/ack",new NodeAck(Instances[i],id,desired.Envelope.ConfigVersion,desired.Envelope.DeploymentSequence,desired.Envelope.PayloadHash,DateTimeOffset.UtcNow,true,null));ack.EnsureSuccessStatusCode();}}
    public async Task<Guid> PublishAsync(bool acknowledge=true) {var id=await ReadyAsync();await BuildAsync(id);if(acknowledge) await AckAllAsync(id);return id;}
    public async Task<HttpResponseMessage> NodeAsync(int i,string path,object body) {using var request=new HttpRequestMessage(HttpMethod.Post,path) {Content=JsonContent.Create(body)};request.Headers.Authorization=new("Bearer",secrets[i]);return await Api.Client.SendAsync(request);}
    public async ValueTask DisposeAsync() {await Api.DisposeAsync();foreach(var file in files) File.Delete(file);}
}

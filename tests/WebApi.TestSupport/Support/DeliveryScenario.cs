using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Integration.Tests.Support;
public sealed class DeliveryScenario : IAsyncDisposable
{
    public DeploymentScenario Deployment {get;}=new();
    public ApiFixture Api=>Deployment.Api;
    public ReleaseArtifactDto Artifact {get;private set;}=null!;
    public Guid ReleaseId {get;private set;}
    public async Task InitializeAsync(bool canRecord=true,Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure=null)
    {
        await Deployment.InitializeAsync(configure);
        if(canRecord){await using var db=Api.Context();var permission=new Permission{Code="release.test.record",Module="release",Name="登记人工测试"};db.Add(permission);db.Add(new RolePermission{RoleId=await db.Set<UserRole>().Where(x=>x.UserId==Api.User.Id).Select(x=>x.RoleId).SingleAsync(),PermissionId=permission.Id});await db.SaveChangesAsync();}
        ReleaseId=await Deployment.PublishAsync();await using(var db=Api.Context()){var environment=await db.Set<EnvironmentRecord>().SingleAsync();environment.IsProduction=false;environment.GatewayPublicUrl="https://source-gateway.example";environment.BasePath="/api";await db.SaveChangesAsync();}
        using var created=await ApiFixture.CommandAsync(Api.Client,$"/api/v1/releases/{ReleaseId}/artifacts");created.EnsureSuccessStatusCode();Artifact=(await created.Content.ReadFromJsonAsync<ReleaseArtifactDto>())!;
    }
    public async Task<HttpResponseMessage> UploadAsync(byte[] bytes,string type="text/plain",Guid? artifact=null,Guid? promotion=null,string? key=null,bool chunked=false)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/verification-reports?"+(promotion is Guid p?$"promotionId={p}":$"artifactId={artifact??Artifact.Id}")){Content=chunked?new ChunkedContent(bytes):new ByteArrayContent(bytes)};
        request.Content.Headers.ContentType=new(type);request.Headers.Add("X-CSRF-Token",await Api.CsrfAsync());request.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString("N"));return await Api.Client.SendAsync(request);
    }
    public Task<HttpResponseMessage> RecordAsync(string result="Passed",string type="InterfaceFunction",Guid? report=null,DateTimeOffset? started=null,DateTimeOffset? finished=null,string? key=null)=>ApiFixture.CommandAsync(Api.Client,$"/api/v1/release-artifacts/{Artifact.Id}/verifications",new RecordVerificationRequest(type,result,started??DateTimeOffset.UtcNow.AddMinutes(-5),finished??DateTimeOffset.UtcNow.AddMinutes(-1),report,"人工登记"),key);
    public async ValueTask DisposeAsync()=>await Deployment.DisposeAsync();
    private sealed class ChunkedContent(byte[] bytes):HttpContent
    {
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override Task SerializeToStreamAsync(Stream stream,System.Net.TransportContext? context)=>stream.WriteAsync(bytes).AsTask();
    }
}

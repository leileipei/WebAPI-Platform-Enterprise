using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Runtime;
using WebApi.Gateway.Configuration;
namespace WebApi.Gateway.Workers;
public sealed class NodeClient : IDisposable
{
    private readonly GatewaySettings settings;private readonly HttpClient client;private readonly SemaphoreSlim registration=new(1,1);public Guid? NodeId {get;private set;}
    public NodeClient(GatewaySettings settings)
    {this.settings=settings;var secret=File.ReadAllText(settings.SecretFile).Trim();if(secret.Length is <32 or >1024||secret.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Invalid gateway node secret file.");client=new(new SocketsHttpHandler {UseCookies=false,AllowAutoRedirect=false}) {BaseAddress=settings.ControlPlaneUrl,Timeout=TimeSpan.FromSeconds(3)};client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",secret);}
    public async Task RegisterAsync(CancellationToken ct=default)
    {await registration.WaitAsync(ct);try {if(NodeId is not null) return;using var response=await client.PostAsJsonAsync("/internal/v1/nodes/register",new RegisterNodeRequest(settings.EnvironmentId,settings.NodeName,settings.InstanceId,"1.0.0"),ct);response.EnsureSuccessStatusCode();NodeId=(await response.Content.ReadFromJsonAsync<RegisteredNode>(ct))!.NodeId;}finally {registration.Release();}}
    public async Task<DesiredConfigResponse?> DesiredAsync(CancellationToken ct)
    {await RegisterAsync(ct);using var response=await client.GetAsync($"/internal/v1/nodes/{NodeId}/desired",ct);if(response.StatusCode==HttpStatusCode.NoContent) return null;response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<DesiredConfigResponse>(ct);}
    public async Task AckAsync(SnapshotEnvelope envelope,bool success,string? code,CancellationToken ct)
    {await RegisterAsync(ct);using var response=await client.PostAsJsonAsync($"/internal/v1/nodes/{NodeId}/ack",new NodeAck(settings.InstanceId,envelope.ReleaseId,envelope.ConfigVersion,envelope.DeploymentSequence,envelope.PayloadHash,DateTimeOffset.UtcNow,success,code),ct);if(response.StatusCode==HttpStatusCode.Conflict) return;response.EnsureSuccessStatusCode();}
    public async Task HeartbeatAsync(RuntimeGenerationStore store,CancellationToken ct)
    {await RegisterAsync(ct);var generation=store.Current;using var response=await client.PostAsJsonAsync($"/internal/v1/nodes/{NodeId}/heartbeat",new NodeHeartbeat(settings.InstanceId,generation?.Envelope.ConfigVersion??0,generation?.Envelope.DeploymentSequence??0,generation is null?"NotReady":"Ready"),ct);response.EnsureSuccessStatusCode();}
    public void Dispose() {client.Dispose();registration.Dispose();}
}

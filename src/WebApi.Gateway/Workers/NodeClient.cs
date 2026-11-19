using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Security;
namespace WebApi.Gateway.Workers;
public sealed class NodeClient : IDisposable
{
    private readonly GatewaySettings settings;private readonly HttpClient client;private readonly SemaphoreSlim registration=new(1,1);public Guid? NodeId {get;private set;}
    private readonly string[] snapshotSchemas;
    private readonly string[]? securityCapabilities;
    private readonly HttpClient? securityClient;
    public NodeClient(GatewaySettings settings,GatewaySecurityEvidence? governance=null)
    {
        this.settings=settings;
        if(governance is not null&&settings.ControlPlaneUrl.Scheme!="https"&&!settings.ControlPlaneUrl.IsLoopback)
            throw new InvalidOperationException("Governance node credentials require an HTTPS control plane.");
        snapshotSchemas=governance is null?["2.0","2.1","2.2","2.3"]:["2.0","2.1","2.2","2.3","2.4"];
        securityCapabilities=governance is null?null:["security-ledger-v1"];
        var secret=File.ReadAllText(settings.SecretFile).Trim();if(secret.Length is <32 or >1024||secret.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Invalid gateway node secret file.");
        client=CreateClient(TimeSpan.FromSeconds(3));
        if(governance is not null) securityClient=CreateClient(TimeSpan.FromSeconds(16));

        HttpClient CreateClient(TimeSpan timeout)
        {
            var result=new HttpClient(new SocketsHttpHandler {UseCookies=false,AllowAutoRedirect=false})
                {BaseAddress=settings.ControlPlaneUrl,Timeout=timeout,MaxResponseContentBufferSize=48L*1024*1024};
            result.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",secret);
            return result;
        }
    }
    public async Task RegisterAsync(CancellationToken ct=default)
    {await registration.WaitAsync(ct);try {if(NodeId is not null) return;using var response=await client.PostAsJsonAsync("/internal/v1/nodes/register",new RegisterNodeRequest(settings.EnvironmentId,settings.NodeName,settings.InstanceId,"1.1.0",snapshotSchemas,securityCapabilities),ct);response.EnsureSuccessStatusCode();NodeId=(await response.Content.ReadFromJsonAsync<RegisteredNode>(ct))!.NodeId;}finally {registration.Release();}}
    public async Task<DesiredConfigResponse?> DesiredAsync(CancellationToken ct)
    {await RegisterAsync(ct);var query=securityCapabilities is null?"":$"?instanceId={settings.InstanceId}";using var response=await (securityClient??client).GetAsync($"/internal/v1/nodes/{NodeId}/desired{query}",ct);if(response.StatusCode==HttpStatusCode.NoContent) return null;response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<DesiredConfigResponse>(ct);}
    public async Task AckAsync(SnapshotEnvelope envelope,bool success,string? code,CancellationToken ct)
    {await RegisterAsync(ct);using var response=await (securityClient??client).PostAsJsonAsync($"/internal/v1/nodes/{NodeId}/ack",new NodeAck(settings.InstanceId,envelope.ReleaseId,envelope.ConfigVersion,envelope.DeploymentSequence,envelope.PayloadHash,DateTimeOffset.UtcNow,success,code),ct);if(response.StatusCode==HttpStatusCode.Conflict) return;response.EnsureSuccessStatusCode();}
    public async Task HeartbeatAsync(RuntimeGenerationStore store,CancellationToken ct,IGovernanceAdmissionStateProvider? governance=null)
    {await RegisterAsync(ct);var generation=store.Current;var ready=generation is not null&&GovernanceReadiness.CanUse(generation,governance);using var response=await client.PostAsJsonAsync($"/internal/v1/nodes/{NodeId}/heartbeat",new NodeHeartbeat(settings.InstanceId,generation?.Envelope.ConfigVersion??0,generation?.Envelope.DeploymentSequence??0,ready?"Ready":"NotReady",snapshotSchemas,securityCapabilities),ct);response.EnsureSuccessStatusCode();}

    internal async Task<SecurityNodeMaterial> SecurityMaterialAsync(CancellationToken ct)
    {
        await RegisterAsync(ct);
        var security=securityClient??throw new InvalidOperationException("Gateway security evidence is unavailable.");
        using var response=await security.PostAsync($"/internal/v1/nodes/{NodeId}/security/material?instanceId={settings.InstanceId}",null,ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SecurityNodeMaterial>(ct)??throw new InvalidDataException("Missing node security material.");
    }

    internal Task RecordSecurityProofAsync(CurrentSecurityProof proof,CancellationToken ct) =>
        SendSecurityReportAsync("current-proof",proof,ct);

    internal Task RecordSecurityAckAsync(SecurityAck ack,CancellationToken ct) =>
        SendSecurityReportAsync("ack",ack,ct);

    private async Task SendSecurityReportAsync<T>(string endpoint,T report,CancellationToken ct)
    {
        var security=securityClient??throw new InvalidOperationException("Gateway security evidence is unavailable.");
        using var response=await security.PostAsJsonAsync($"/internal/v1/nodes/{NodeId}/security/{endpoint}",report,ct);
        response.EnsureSuccessStatusCode();
    }
    // Reuse the node registration owner, bearer and three-second client deadline.
    internal async Task<HttpResponseMessage> PostUsageAsync<T>(string resource,T request,CancellationToken ct)
    {await RegisterAsync(ct);return await client.PostAsJsonAsync($"/internal/v1/nodes/{NodeId}/credential-usage/{resource}",request,WebApi.Contracts.CredentialUsage.UsageProtocolJson.Options,ct);}
    public void Dispose() {securityClient?.Dispose();client.Dispose();registration.Dispose();}
}

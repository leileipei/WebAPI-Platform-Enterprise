using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Gateway.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Runtime;
using Yarp.ReverseProxy.Configuration;
namespace WebApi.Gateway.Configuration;
public sealed record ActivationResult(bool Applied,string? ErrorCode);
public sealed class SnapshotActivation(GatewaySettings settings,RuntimeGenerationStore generations,EnterpriseProxyConfigProvider provider,AdmissionGate admission,LkgStore lkg,IConfigValidator validator,UpstreamAddressPolicy addresses,GatewayCacheSettings cacheSettings,GatewayBodyTransformSettings bodySettings,IGovernanceAdmissionStateProvider? governance=null)
{
    private readonly SemaphoreSlim activation=new(1,1);
    public async Task<ActivationResult> ApplyAsync(SnapshotEnvelope envelope,ReadOnlyMemory<byte> payload,CancellationToken ct=default)=>await ApplyCoreAsync(envelope,payload,true,ct);
    public async Task RestoreAsync(CancellationToken ct=default)
    {
        var candidates=await lkg.ReadCandidatesAsync(ct);
        var hasGovernedSnapshot=candidates.Any(IsGoverned);
        for(var i=0;i<candidates.Count;i++)
        {
            var value=candidates[i];
            if(hasGovernedSnapshot&&!IsGoverned(value)) continue;
            var result=await ApplyCoreAsync(value.Envelope,value.Payload,false,ct);
            if(result.Applied)
            {
                if(i>0) {try {await lkg.RestoreAsync(value,ct);}catch(Exception e) when(e is IOException or UnauthorizedAccessException) {}}
                return;
            }
            if(result.ErrorCode=="security_state_unavailable") return;
        }
    }

    private static bool IsGoverned(DesiredConfigResponse desired) =>
        SnapshotDialectClassifier.Classify(desired.Payload)==SnapshotDialect.Governance24;
    private async Task<ActivationResult> ApplyCoreAsync(SnapshotEnvelope envelope,ReadOnlyMemory<byte> payload,bool persist,CancellationToken ct)
    {
        await activation.WaitAsync(ct);
        try
        {
            RuntimeSnapshot snapshot;EnterpriseProxyConfigProvider.Config config;
            try {snapshot=RedisSnapshotStore.ReadArtifact(settings.EnvironmentId,envelope,payload.Span,governance is not null);cacheSettings.Validate(snapshot);bodySettings.Validate(snapshot);foreach(var cluster in snapshot.Clusters) foreach(var dst in cluster.Destinations) addresses.Validate(dst.Address);config=provider.Build(snapshot,envelope.DeploymentSequence);foreach(var c in config.Clusters) if((await validator.ValidateClusterAsync(c)).Count>0) return new(false,"yarp_rejected_config");foreach(var r in config.Routes) if((await validator.ValidateRouteAsync(r)).Count>0) return new(false,"yarp_rejected_config");}
            catch(Exception e) when(e is JsonException or InvalidDataException or ApiException or ArgumentException or NullReferenceException or OverflowException) {return new(false,"invalid_snapshot");}
            var current=generations.Current;
            if(snapshot.SchemaVersion!="2.4"&&(current?.Snapshot.SchemaVersion=="2.4"||
                current is null&&(await lkg.ReadCandidatesAsync(ct)).Any(IsGoverned))) return new(false,"invalid_snapshot");
            if(current is not null&&envelope.DeploymentSequence<=current.Envelope.DeploymentSequence)
            {
                if(envelope==current.Envelope&&payload.Span.SequenceEqual(current.Payload.Span))
                    return GovernanceReadiness.CanUse(current,governance)?new(true,null):new(false,"security_state_unavailable");
                return new(false,envelope.DeploymentSequence<current.Envelope.DeploymentSequence?"stale_sequence":"sequence_conflict");
            }
            using var gate=await admission.EnterAsync(ct);var prior=current is null?null:new DesiredConfigResponse(current.Envelope,current.Payload.ToArray());var previous=provider.Current;
            var pending=new RuntimeGeneration(envelope,payload.ToArray(),snapshot);
            if(!GovernanceReadiness.CanUse(pending,governance)) return new(false,"security_state_unavailable");
            try {if(persist) await lkg.WriteAtomicAsync(envelope,payload,ct);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) {return new(false,"lkg_write_denied");}
            generations.Register(pending);bool applied;
            try {applied=await provider.SwitchAsync(config).WaitAsync(TimeSpan.FromSeconds(10),CancellationToken.None);}catch(TimeoutException) {applied=false;}
            if(applied&&GovernanceReadiness.CanUse(pending,governance)) {generations.Activate(envelope.DeploymentSequence);return new(true,null);}
            // Every provider switch has a fresh token, including recovery; a canceled token is never reused.
            var restore=new EnterpriseProxyConfigProvider.Config(previous.Routes,previous.Clusters);try {await provider.SwitchAsync(restore).WaitAsync(TimeSpan.FromSeconds(10));}catch(TimeoutException) {}
            generations.Discard(envelope.DeploymentSequence);if(persist) await lkg.RestoreAsync(prior,CancellationToken.None);return new(false,applied?"security_state_unavailable":"yarp_rejected_config");
        }
        finally {activation.Release();}
    }
}
